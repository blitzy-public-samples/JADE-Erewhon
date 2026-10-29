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
