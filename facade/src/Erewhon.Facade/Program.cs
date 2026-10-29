// Host and composition root of the Erewhon strangler facade. It owns the caller addresses of the JADE
// ErewhonREST (REST, 65000) and WebServiceHTTP (SOAP, 21001) applications of
// Erewhon/ErehwhonExternalInterfacesSchema.ddx, plus an internal admin listener (9090), and forwards
// every REST and SOAP request unchanged to the target its routing entry names. The only responses the
// facade builds itself are the backend-unavailable and backend-timed-out failures and the published
// WSDL's address.
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

        // Plain HTTP/1.1 on every listener, as the JADE applications serve (_webIsSecure false).
        kestrel.Listen(address, ListenerPort(facade.RestPort, nameof(FacadeOptions.RestPort)), listen => listen.Protocols = HttpProtocols.Http1);
        kestrel.Listen(address, ListenerPort(facade.SoapPort, nameof(FacadeOptions.SoapPort)), listen => listen.Protocols = HttpProtocols.Http1);
        kestrel.Listen(address, ListenerPort(facade.AdminPort, nameof(FacadeOptions.AdminPort)), listen => listen.Protocols = HttpProtocols.Http1);
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
    // not a catalogue operation, stops start-up with one message per bad entry.
    builder.Services.AddOptions<RoutingOptions>()
        .Bind(builder.Configuration.GetSection(RoutingOptions.SectionName))
        .ValidateOnStart();
    builder.Services.AddSingleton<IValidateOptions<RoutingOptions>, RoutingOptionsValidator>();

    builder.Services.AddHttpForwarder();
    builder.Services.AddSingleton<OperationRouter>();
    builder.Services.AddSingleton<PassThroughTransformer>();
    builder.Services.AddSingleton<SoapFaultWriter>();
    builder.Services.AddSingleton<BackendForwarder>();
    builder.Services.AddSingleton<WsdlDocument>();

    builder.Services.AddOpenApi(CatalogueOpenApiTransformers.DocumentName, openApi => openApi.AddCatalogueTransformers());

    var app = builder.Build();

    // Loads the published WSDL now, so its presence or absence is logged at start-up. An absent WSDL
    // never stops the facade: WSDL requests are then forwarded to the default target unchanged.
    app.Services.GetRequiredService<WsdlDocument>();

    // Only the implicit routing and endpoint middleware runs; every endpoint is bound to its own listener.
    var catalogue = app.Services.GetRequiredService<OperationCatalogue>();
    app.MapRestEndpoints(catalogue);
    app.MapSoapEndpoints();
    app.MapAdminEndpoints();

    // POST /admin/shutdown on the admin listener stops the host, and Run returns.
    app.Run();
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
// so an out-of-range port is reported here under its own key. Port 0 is rejected too: the matcher
// policy needs the exact port each listener is bound to.
static int ListenerPort(int port, string key) =>
    port is >= 1 and <= IPEndPoint.MaxPort
        ? port
        : throw new InvalidOperationException(
            $"{FacadeOptions.SectionName}:{key} must be between 1 and {IPEndPoint.MaxPort}; the value is {port}.");

/// <summary>
/// The facade's entry point, declared public so that <c>WebApplicationFactory&lt;Program&gt;</c> in
/// <c>Erewhon.Facade.Tests</c> can host it on real Kestrel listeners.
/// </summary>
public partial class Program;
