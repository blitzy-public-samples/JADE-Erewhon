// Host and composition root of the Erewhon strangler facade. It owns the caller addresses of the JADE
// ErewhonREST (REST, 65000) and WebServiceHTTP (SOAP, 21001) applications of
// Erewhon/ErehwhonExternalInterfacesSchema.ddx, plus an internal admin listener (9090), and forwards
// every REST and SOAP request unchanged to the target its routing entry names. On the REST and SOAP
// listeners the facade builds only the backend-unavailable and backend-timed-out failure responses
// (REST 502 or 504 problem+json, SOAP Fault) and the published WSDL's soap:address. Every other failed
// call is aborted rather than answered, except that an exception escaping request handling gets the
// server's own 500 for an unhandled exception. The admin listener's own responses (health, admin info,
// the OpenAPI document, shutdown and its 404 catch-all) are internal and never served on the REST or
// SOAP listeners.
//
// Nothing that could rewrite or reject a forwarded request or response is registered: no
// authentication, authorization, HTTPS redirection, HSTS, CORS, antiforgery, response compression,
// problem details, exception handler or status-code pages. The admin endpoints are contained only by
// listener isolation (ListenerPortMatcherPolicy dispatches by the socket a request arrived on, never by
// its Host header) and by network placement, because the current JADE services carry no
// authentication or TLS either.

using System.Net;
using System.Reflection;
using Erewhon.Contracts.Catalogue;
using Erewhon.Contracts.Hosting;
using Erewhon.Contracts.Matching;
using Erewhon.Facade;
using Erewhon.Facade.Admin;
using Erewhon.Facade.Forwarding;
using Erewhon.Facade.OpenApi;
using Erewhon.Facade.Rest;
using Erewhon.Facade.Routing;
using Erewhon.Facade.Soap;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Options;

// One boundary covers everything from reading configuration to the host stopping. A routing table or
// setting that fails validation, a bind address that does not parse or a port that cannot be bound ends
// this process with one line on standard error and exit code 1. Left unhandled, the exception would
// abort the runtime with SIGABRT, and where core dumps are enabled leave a core file of over 100 MB in
// the working directory (facade/ under harness/scripts/run-local.sh). When the entry point is run
// in-process by another assembly (WebApplicationFactory<Program> in Erewhon.Facade.Tests), the exception
// is left to that host instead: it reports the start-up failure only if the entry point throws.
try
{
    // The content root is left as the host chooses it: WebApplicationFactory sets it in tests, the
    // image runs from /app, and harness/scripts/run-local.sh passes --contentRoot.
    var builder = WebApplication.CreateBuilder(args);

    // `dotnet run` from facade/ makes facade/ the content root, where no appsettings.json exists. The
    // copy in the build output is then added as the lowest-precedence source, so environment variables,
    // the command line and test settings still override every value in it.
    const string SettingsFileName = "appsettings.json";
    var baseDirectorySettings = Path.Combine(AppContext.BaseDirectory, SettingsFileName);
    if (!File.Exists(Path.Combine(builder.Environment.ContentRootPath, SettingsFileName))
        && File.Exists(baseDirectorySettings))
    {
        var settingsSource = new JsonConfigurationSource
        {
            Path = baseDirectorySettings,
            Optional = true,
            ReloadOnChange = false,
        };
        settingsSource.ResolveFileProvider();
        builder.Configuration.Sources.Insert(0, settingsSource);
    }

    // One JSON line per event, which carries BackendForwarder's per-call line (surface, operation,
    // target, status, duration, outcome). UseUtcTimestamp takes effect only once a format is set.
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(console =>
    {
        console.IncludeScopes = false;
        console.UseUtcTimestamp = true;
        console.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    });

    // Framework request logging prints the raw request path, request line and header text, and REST path
    // segments carry agent and client names. YARP's forwarder logs each destination URL and, for every
    // failed call, a warning with the full exception stack that repeats what the call's one line records.
    // These floors hold whatever the Logging configuration says. Hosting stays at Warning rather than off,
    // so its start-up exception is still logged.
    builder.Logging
        .AddLogLevelFloor("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning)
        .AddLogLevelFloor("Microsoft.AspNetCore.Routing", LogLevel.Warning)
        .AddLogLevelFloor("Microsoft.AspNetCore.Server.Kestrel.BadRequests", LogLevel.Warning)
        .AddLogLevelFloor("Yarp", LogLevel.Error);

    // POST /admin/shutdown must end the process well inside the harness's 15 s shutdown limit.
    builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(5));

    // The options are read from the context's configuration inside the callback, which runs while the
    // app is built and after every configuration source has been added, so Facade__* variables,
    // --Facade:* switches and test UseSetting("Facade:RestPort", ...) values all apply. The explicit
    // Listen calls replace ASPNETCORE_HTTP_PORTS and any URLs setting.
    builder.WebHost.ConfigureKestrel((context, kestrel) =>
    {
        var facade = context.Configuration.GetSection(FacadeOptions.SectionName).Get<FacadeOptions>() ?? new FacadeOptions();
        var address = facade.GetBindAddress();

        // A forwarded response carries the backend's headers, never a Server header of the facade's own.
        kestrel.AddServerHeader = false;

        // The facade never answers 413: any body-size limit stays the backend's own.
        kestrel.Limits.MaxRequestBodySize = null;

        // Nor 414 or 431 for a request line or header block the request buffer can hold: it is
        // forwarded, and the backend's own limits decide. The buffer is the bound because Kestrel
        // refuses to start with either head limit above MaxRequestBufferSize, which stays bounded
        // (Kestrel's 1 MiB default) since it is also the back-pressure threshold for a body streamed
        // to a slow backend. Kestrel refuses a header block only once it holds more than the limit
        // plus the block's closing CRLF, and stops reading when the buffer is full, so the header
        // limit stays three bytes below the buffer: at the full size, a block just over it would wait
        // for the request-headers timeout (408) instead of being refused with 431. The header total
        // already bounds how many headers fit. Only a larger head is answered by the facade itself, with
        // 414 or 431 rather than forwarded, and that bound is the accepted limit recorded as
        // KL-FACADE-REQUEST-HEAD in harness/config/known-limitations.json.
        const int RequestBufferSize = 1024 * 1024;
        kestrel.Limits.MaxRequestBufferSize = RequestBufferSize;
        kestrel.Limits.MaxRequestLineSize = RequestBufferSize;
        kestrel.Limits.MaxRequestHeadersTotalSize = RequestBufferSize - 3;
        kestrel.Limits.MaxRequestHeaderCount = int.MaxValue;

        // Header values are read and written as Latin-1, one char per byte, as BackendForwarder's handler
        // writes and reads them, so non-ASCII header bytes pass through in both directions unchanged;
        // ASCII values decode and encode exactly as before.
        kestrel.RequestHeaderEncodingSelector = static _ => System.Text.Encoding.Latin1;
        kestrel.ResponseHeaderEncodingSelector = static _ => System.Text.Encoding.Latin1;

        // Every port is checked on its own and against the others before any listener is added.
        var restPort = ListenerPort(facade.RestPort, nameof(FacadeOptions.RestPort));
        var soapPort = ListenerPort(facade.SoapPort, nameof(FacadeOptions.SoapPort));
        var adminPort = ListenerPort(facade.AdminPort, nameof(FacadeOptions.AdminPort));
        RequireDistinctPorts(
            (nameof(FacadeOptions.RestPort), restPort),
            (nameof(FacadeOptions.SoapPort), soapPort),
            (nameof(FacadeOptions.AdminPort), adminPort));

        // Plain HTTP/1.1 on every listener, as the JADE applications serve (_webIsSecure false).
        // The forwarding REST and SOAP listeners also capture each request's Connection lines as sent.
        kestrel.Listen(address, restPort, listen =>
        {
            listen.Protocols = HttpProtocols.Http1;
            listen.UseRawConnectionHeaderCapture();
        });
        kestrel.Listen(address, soapPort, listen =>
        {
            listen.Protocols = HttpProtocols.Http1;
            listen.UseRawConnectionHeaderCapture();
        });
        kestrel.Listen(address, adminPort, listen => listen.Protocols = HttpProtocols.Http1);
    });

    builder.Services.AddOptions<FacadeOptions>().Bind(builder.Configuration.GetSection(FacadeOptions.SectionName));

    builder.Services.AddOptions<BackendOptions>()
        .Bind(builder.Configuration.GetSection(BackendOptions.SectionName))
        .Validate(backend => backend.TimeoutSeconds > 0, $"{BackendOptions.TimeoutKey} must be greater than 0.")
        .ValidateOnStart();

    // The matcher policy compares each endpoint's listener port with the connection's local port, so it
    // takes the ports from the same bound FacadeOptions Kestrel listens on: a key absent from
    // configuration then gets the same default on both sides instead of an unset 0.
    builder.Services.AddOptions<ListenerPorts>().Configure<IOptions<FacadeOptions>>((ports, facade) =>
    {
        ports.RestPort = facade.Value.RestPort;
        ports.SoapPort = facade.Value.SoapPort;
        ports.AdminPort = facade.Value.AdminPort;
    });
    builder.Services.AddListenerPortMatcherPolicy();

    builder.Services.AddSingleton(OperationCatalogue.Default);
    builder.Services.AddSingleton<RestOperationMatcher>();
    builder.Services.AddSingleton<SoapOperationResolver>();

    // A routing table that leaves a catalogue operation without a usable target, or lists a key that is
    // not a catalogue operation, stops start-up with one message per bad entry. The binder would silently
    // ignore a misspelled key and drop a target whose TimeoutSeconds is not an integer, so the raw section
    // is checked first, by path, in a configure step that runs before Bind each time the options are built
    // (start-up and every reload). Strict binding stays as a safety net, and Bind also registers the
    // change-token source that propagates reloads.
    var routingSection = builder.Configuration.GetSection(RoutingOptions.SectionName);
    builder.Services.AddOptions<RoutingOptions>()
        .Configure(_ => RoutingOptionsValidator.ThrowIfConfigurationInvalid(Options.DefaultName, routingSection))
        .Bind(routingSection, binder => binder.ErrorOnUnknownConfiguration = true)
        .ValidateOnStart();
    builder.Services.AddSingleton<IValidateOptions<RoutingOptions>, RoutingOptionsValidator>();

    builder.Services.AddHttpForwarder();
    builder.Services.AddSingleton<OperationRouter>();

    // Publishes the routing table of the validated start-up options before the listeners start, so a
    // reload rejected before the first request still leaves that table in service.
    builder.Services.AddHostedService<OperationRouterStartup>();

    builder.Services.AddSingleton<PassThroughTransformer>();
    builder.Services.AddSingleton<SoapFaultWriter>();
    builder.Services.AddSingleton<BackendForwarder>();
    builder.Services.AddSingleton<WsdlDocument>();

    builder.Services.AddOpenApi(CatalogueOpenApiTransformers.DocumentName, openApi => openApi.AddCatalogueTransformers());

    var app = builder.Build();

    // When the facade is its own process, the host is disposed on every path out of this block, so a
    // failure before it runs, such as a WSDL path that cannot be resolved, still flushes the console log
    // and releases the host's services and configuration sources; after RunAsync, which disposes the
    // host itself, this second disposal does nothing. In-process, the host is left to the caller that
    // captured it while it was built (WebApplicationFactory<Program>), which goes on reading the host's
    // services on its own thread: disposing them here could make that caller report an
    // ObjectDisposedException in place of the start-up failure.
    await using var ownedApp = Assembly.GetEntryAssembly() == typeof(Program).Assembly ? app : null;

    // Loads the published WSDL now, so its presence or absence is logged at start-up. An absent WSDL
    // never stops the facade: WSDL requests are then forwarded to the default target unchanged.
    app.Services.GetRequiredService<WsdlDocument>();

    // Besides the implicit routing and endpoint middleware, only the admin-socket header middleware runs:
    // it adds X-Content-Type-Options to admin responses and leaves REST and SOAP requests and responses
    // untouched. Every endpoint is bound to its own listener.
    var catalogue = app.Services.GetRequiredService<OperationCatalogue>();
    app.UseAdminResponseHeaders();
    app.MapRestEndpoints(catalogue);
    app.MapSoapEndpoints();
    app.MapAdminEndpoints();

    // POST /admin/shutdown on the admin listener stops the host, and RunAsync returns.
    await app.RunAsync();
}
catch (Exception exception) when (Assembly.GetEntryAssembly() == typeof(Program).Assembly)
{
    // A start-up failure has usually been logged by the host already, with its stack trace.
    Console.Error.WriteLine(
        $"{AdminEndpoints.ComponentName}: stopped by {exception.GetType().FullName}: {exception.Message.ReplaceLineEndings(" ")}");
    return 1;
}

return 0;

// Kestrel options are built while the app is built, before the ListenerPorts start-up validation runs,
// so an out-of-range or shared port is reported here under its own key. Port 0 is rejected too: the
// matcher policy needs the exact port each listener is bound to.
static int ListenerPort(int port, string key) =>
    port is >= 1 and <= IPEndPoint.MaxPort
        ? port
        : throw new InvalidOperationException(
            $"{FacadeOptions.SectionName}:{key} must be between 1 and {IPEndPoint.MaxPort}; the value is {port}.");

// The matcher policy tells the listeners apart only by port, so each needs its own. Every pair that
// shares one is named in a single message, in the order the listeners are given.
static void RequireDistinctPorts(params (string Key, int Port)[] listeners)
{
    var conflicts = new List<string>();
    for (var first = 0; first < listeners.Length; first++)
    {
        for (var second = first + 1; second < listeners.Length; second++)
        {
            if (listeners[first].Port == listeners[second].Port)
            {
                conflicts.Add(
                    $"{FacadeOptions.SectionName}:{listeners[first].Key} and {FacadeOptions.SectionName}:{listeners[second].Key} are both {listeners[first].Port}; each listener needs its own port.");
            }
        }
    }

    if (conflicts.Count > 0)
    {
        throw new InvalidOperationException(string.Join(" ", conflicts));
    }
}

/// <summary>
/// The facade's entry point, declared public so that <c>WebApplicationFactory&lt;Program&gt;</c> in
/// <c>Erewhon.Facade.Tests</c> can host it on real Kestrel listeners.
/// </summary>
public partial class Program;
