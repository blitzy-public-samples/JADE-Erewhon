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
    private const string ServerFaultCode = "soap:Server";
    private const string UnavailableText = "Backend unavailable";
    private const string TimedOutText = "Backend timed out";

    private const int SlowBackendTimeoutSeconds = 2;

    // A REST operation with a body; the declared body is never completed, one byte every 0.5 s.
    private const string TrickledUploadOperationId = "Rest.postCreateClient";
    private const string JsonContentType = "application/json";
    private const int TrickledBodyLength = 1000;
    private const string TimeoutOutcome = "timeout";

    private static readonly XNamespace SoapEnvelope = "http://schemas.xmlsoap.org/soap/envelope/";

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

        Assert.Equal(504, response.Status);
        using (JsonDocument problem = JsonDocument.Parse(response.Body))
        {
            Assert.Equal(TimedOutText, StringMember(problem.RootElement, "title"));
        }

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
// report counts test classes whose names end in "FailureBehaviourTests" as the unreachable and
// timeout check, and this name matches none.
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

// BackendForwarder's own handling of every other failure (class BackendForwarderTests) sits here,
// beside the unreachable and timeout cases it completes, as a class of its own: the results report
// counts test classes whose names end in "FailureBehaviourTests" as the unreachable and timeout check,
// and this name matches none.
//
// BackendForwarder's handling of every call that is neither an unreachable backend nor an expired
// deadline, which FailureBehaviourTests proves: the one per-call line (event 1001, BackendCall) with the
// outcome, level and status of each failure kind, exceptions, the bounded failure-response write, caller
// aborts and the backend's own answers to uploads. The classification and write cases construct the
// forwarder directly over a fake IHttpForwarder that reports each YARP error as YARP does; the caller and
// upload cases run the facade on real loopback Kestrel listeners.
//
// Failures are identified across runs by a signature over the exception type, the failing line and the
// assertion message, so every message and Assert.Equal operand here is run-invariant: no port, URL,
// elapsed time or GUID. Log entries are never passed to Assert.Single or Assert.Equal, because their dump
// holds durations; timing bounds are Assert.True calls with fixed messages.
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
    private const string BackendResetCause = "backend-reset";
    private const string ConnectFailedCause = "connect-failed";
    private const string NameNotResolvedCause = "name-not-resolved";
    private const string SendFailedCause = "send-failed";
    private const string ResponseEndedCause = "response-ended";

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

    // The early-rejecting backend's own body-size limit, and an upload well beyond it and beyond the
    // facade's 1 MiB 100-continue threshold.
    private const long BackendBodyLimit = 64 * 1024;
    private const int OverLimitBodyLength = 2 * 1024 * 1024;

    // A chunked upload, which a backend can reject only once it has read up to its limit: 40 MiB against
    // Kestrel's default limit of 30,000,000 bytes, as the JADE stand-in has it.
    private const long DefaultBackendBodyLimit = 30_000_000;
    private const int ChunkedOverLimitBodyLength = 40 * 1024 * 1024;

    // The limit of a backend that rejects an upload the caller never completes, well before its end.
    private const long SmallBackendBodyLimit = 1024 * 1024;

    private static readonly TimeSpan SlowBackendDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CallerPatience = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LogWaitBound = TimeSpan.FromSeconds(5);

    // A 1 s deadline plus the forwarder's 2 s failure-write budget, with margin; a hang is cut off at 10 s.
    private static readonly TimeSpan BoundedWriteLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Every YARP error kind ends in exactly one line whose outcome, level and status say what happened,
    /// and the caller gets the facade response, the bare 500, nothing, or an aborted connection that
    /// outcome stands for. Caller aborts never report YARP's placeholder status; a backend that dropped
    /// the connection is never reported unavailable.
    /// </summary>
    [Theory]
    [InlineData(ForwarderError.None, NoCause, false, true, Forwarded, LogLevel.Information, 200, false, null)]
    [InlineData(ForwarderError.Request, RefusedCause, false, false, Unavailable, LogLevel.Warning, 502, false, UnavailableTitle)]
    [InlineData(ForwarderError.RequestTimedOut, ConnectTimeoutCause, false, false, TimedOut, LogLevel.Warning, 504, false, TimedOutTitle)]
    [InlineData(ForwarderError.RequestCanceled, CallerCancelledCause, true, false, Aborted, LogLevel.Information, 0, false, null)]
    [InlineData(ForwarderError.RequestBodyCanceled, TruncatedUploadCause, false, false, Aborted, LogLevel.Information, 0, false, null)]
    [InlineData(ForwarderError.RequestBodyClient, CallerResetCause, false, false, Aborted, LogLevel.Information, 0, false, null)]
    [InlineData(ForwarderError.ResponseBodyCanceled, CallerCancelledCause, true, true, Aborted, LogLevel.Information, 200, false, null)]
    [InlineData(ForwarderError.RequestBodyClient, TempSpaceFullCause, false, false, Failed, LogLevel.Warning, 500, false, null)]
    [InlineData(ForwarderError.RequestBodyDestination, BackendResetCause, false, false, Failed, LogLevel.Warning, 0, true, null)]
    [InlineData(ForwarderError.ResponseBodyDestination, BackendResetCause, true, true, Failed, LogLevel.Warning, 200, true, null)]
    [InlineData(ForwarderError.ResponseHeaders, NoCause, false, false, Failed, LogLevel.Warning, 500, false, null)]
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
        if (expectedStatus == StatusCodes.Status500InternalServerError)
        {
            // The facade's own failure: a bare 500 on a connection that closes after it.
            Assert.Equal(StatusCodes.Status500InternalServerError, call.Context.Response.StatusCode);
            Assert.Equal(0L, call.Context.Response.ContentLength);
            Assert.Equal("close", call.Context.Response.Headers.Connection.ToString());
        }
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
    /// A <see cref="ForwarderError.Request"/> failure is answered "Backend unavailable" only when the
    /// backend could not be reached or received no more than the request head: a failed connect or name
    /// lookup, or a connection that broke before any response and before any body byte was sent, as for a
    /// request without a body or a body still waiting for 100 Continue. A backend that was reached and
    /// dropped an upload it had begun to receive, without an answer, ends the caller's connection
    /// instead, logged <c>failed</c> with status 0.
    /// </summary>
    [Theory]
    [InlineData(ConnectFailedCause, true, true, Unavailable, 502, false)]
    [InlineData(NameNotResolvedCause, true, true, Unavailable, 502, false)]
    [InlineData(RefusedCause, true, true, Unavailable, 502, false)]
    [InlineData(SendFailedCause, true, true, Failed, 0, true)]
    [InlineData(ResponseEndedCause, true, true, Failed, 0, true)]
    [InlineData(SendFailedCause, true, false, Unavailable, 502, false)]
    [InlineData(ResponseEndedCause, true, false, Unavailable, 502, false)]
    [InlineData(SendFailedCause, false, false, Unavailable, 502, false)]
    [InlineData(ResponseEndedCause, false, false, Unavailable, 502, false)]
    public async Task RequestFailureIsUnavailableOnlyWhenBackendWasNotReached(
        string cause, bool carriesBody, bool uploadStarted, string expectedOutcome, int expectedStatus, bool expectedConnectionAbort)
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
        Assert.Equal(expectedOutcome, calls[0].Value("Outcome"));
        Assert.Equal(LogLevel.Warning, calls[0].Level);
        Assert.Equal(expectedStatus, calls[0].Value("Status"));
        Assert.Equal(expectedConnectionAbort, call.Lifetime.AbortCalled);

        if (expectedOutcome == Unavailable)
        {
            using JsonDocument problem = JsonDocument.Parse(call.Body.ToArray());
            Assert.True(problem.RootElement.TryGetProperty("title", out JsonElement title), "problem body has no title member");
            Assert.Equal(UnavailableTitle, title.GetString());
            return;
        }

        Assert.True(call.Body.Length == 0, "facade answered an upload the backend dropped");
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
    /// An upload of 1 MiB or more, or of unknown length, reaches the backend with
    /// <c>Expect: 100-continue</c>; a smaller one is sent exactly as before, and a caller's own
    /// <c>Expect</c> passes unchanged. The body arrives whole either way.
    /// </summary>
    [Theory]
    [InlineData(SmallBodyLength, false, false, null)]
    [InlineData(LargeBodyLength, false, false, ContinueExpectation)]
    [InlineData(SmallBodyLength, true, false, ContinueExpectation)]
    [InlineData(SmallBodyLength, false, true, ContinueExpectation)]
    public async Task UploadAsksForContinueOnlyWhenLargeOrOfUnknownLength(
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
    /// 413 reach the caller unchanged and logged <c>forwarded</c>, even though the caller sent no
    /// <c>Expect</c>: the facade adds no body-size limit and never reports such a backend unavailable. A
    /// declared length over the limit is rejected before any body byte is read; a chunked upload only once
    /// it crosses the limit, while the facade is still sending it.
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
        if (unknownLength)
        {
            Assert.True(backend.BodyBytesRead <= bodyLimit, "backend read more of the upload than its own limit");
        }
        else
        {
            Assert.True(backend.BodyBytesRead == 0, "backend received body bytes before it rejected the upload");
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
    /// A backend that reads an upload and then closes or resets the connection without answering was
    /// reached, so the caller never gets "Backend unavailable": its connection ends as the backend's did,
    /// the call is logged <c>failed</c> with status 0, and the upload is sent once.
    /// </summary>
    [Theory]
    [InlineData(UploadOperationId, false)]
    [InlineData(UploadOperationId, true)]
    [InlineData(SoapOperationId, false)]
    [InlineData(SoapOperationId, true)]
    public async Task BackendThatDropsUploadWithoutAnswerIsNeverReportedUnavailable(string operationId, bool reset)
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
        using HttpRequestMessage message = OperationRequests.For(operation).ToMessage(socket);

        // An answer of any kind, the facade's 502 or Fault included, would let the send succeed.
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.SendAsync(message, ct));

        IReadOnlyList<CapturedLog> calls = await logs.WaitForBackendCallsAsync(1, LogWaitBound, ct);
        Assert.True(calls.Count == 1, "dropped upload did not write exactly one backend call line");
        Assert.Equal(Failed, calls[0].Value("Outcome"));
        Assert.Equal(0, calls[0].Value("Status"));
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
        private long _bodyBytesRead;

        private LimitedBackend(WebApplication app) => _app = app;

        public string BaseUrl { get; private set; } = string.Empty;

        public int Requests => Volatile.Read(ref _requests);

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
