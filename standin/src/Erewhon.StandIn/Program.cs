// Host and composition root of the JADE stand-in: the deterministic fake of the ErewhonREST (REST,
// 65000) and WebServiceHTTP (SOAP, 21001) applications of Erewhon/ErehwhonExternalInterfacesSchema.ddx,
// plus the internal admin listener. The baseline is captured from a local process and replayed against
// the container, so nothing here may make a response depend on the host, the clock or chance.

using System.Globalization;
using System.Net;
using System.Text;
using Erewhon.Contracts;
using Erewhon.Contracts.Catalogue;
using Erewhon.Contracts.Hosting;
using Erewhon.Contracts.Matching;
using Erewhon.StandIn.Admin;
using Erewhon.StandIn.Dto;
using Erewhon.StandIn.Rest;
using Erewhon.StandIn.Soap;
using Erewhon.StandIn.State;
using Microsoft.Extensions.Options;

// Seed files are Windows-1252 (JADE File.Kind_ANSI); the code-pages provider makes that encoding
// available identically on every host.
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

// The content root is the build output, so appsettings.json loads whatever the working directory is.
// StandIn:SeedDirectory is deliberately not resolved against it: StandInState resolves it against the
// working directory (standin/ locally, an absolute /app/seed in the image).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Read from the context's configuration, so --StandIn:* switches and StandIn__* variables apply. The
// explicit Listen calls replace ASPNETCORE_HTTP_PORTS from the aspnet image (Kestrel warns that it
// overrides them).
builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    StandInOptions settings = BindStandInOptions(context.Configuration);
    IPAddress address = ParseBindAddress(settings.BindAddress);

    kestrel.Listen(address, ListenerPort(settings.RestPort, nameof(StandInOptions.RestPort)));
    kestrel.Listen(address, ListenerPort(settings.SoapPort, nameof(StandInOptions.SoapPort)));
    kestrel.Listen(address, ListenerPort(settings.AdminPort, nameof(StandInOptions.AdminPort)));

    // A response carries only the headers its handler sets, plus Kestrel's unavoidable Date.
    kestrel.AddServerHeader = false;
});

builder.Services.AddOptions<StandInOptions>().Bind(builder.Configuration.GetSection(StandInOptions.SectionName));

// The matcher policy compares each endpoint's listener port with the connection's local port, so it
// takes the ports from the same bound StandInOptions Kestrel listens on: a key absent from
// configuration then gets the same default on both sides instead of an unset 0.
builder.Services.AddOptions<ListenerPorts>().Configure<IOptions<StandInOptions>>((ports, standIn) =>
{
    ports.RestPort = standIn.Value.RestPort;
    ports.SoapPort = standIn.Value.SoapPort;
    ports.AdminPort = standIn.Value.AdminPort;
});
builder.Services.AddListenerPortMatcherPolicy();

builder.Services.AddSingleton(OperationCatalogue.Default);
builder.Services.AddSingleton(services => new RestOperationMatcher(services.GetRequiredService<OperationCatalogue>()));
builder.Services.AddSingleton(services => new SoapOperationResolver(services.GetRequiredService<OperationCatalogue>()));
builder.Services.AddSingleton(services => new DtoBuilder(services.GetRequiredService<OperationCatalogue>()));
builder.Services.AddSingleton<StandInState>();

Console.WriteLine(SourceLabels.Banner);

// Build (which applies the Kestrel settings above) and the first seed load run before any endpoint is
// mapped or served. A setting that does not convert, a bind address that does not parse, a missing
// seed directory or an invalid reference date therefore stops the process with one line and exit
// code 1 instead of an unhandled-exception abort or a listener answering from an empty model. After the
// load /admin/info reports generation 1 and mutations 0.
WebApplication app;
StandInState? state = null;
try
{
    app = builder.Build();
    state = app.Services.GetRequiredService<StandInState>();
    state.Reset(ResetKind.Seed);
}
catch (Exception ex)
{
    string seedDirectory = state?.SeedDirectory ?? DescribeConfiguredSeedDirectory(builder.Configuration);
    Console.Error.WriteLine(
        $"{SourceLabels.ConsolePrefix} start-up failed (seed directory '{seedDirectory}'): {ex.Message}");
    return 1;
}

RestEndpoints.Map(app);
SoapEndpoint.Map(app);
AdminEndpoints.Map(app);

try
{
    // POST /admin/shutdown on the admin listener stops the host, and Run returns.
    app.Run();
}
catch (Exception ex)
{
    // Only a host start or stop failure leaves Run, such as a listener that cannot bind or an invalid
    // port layout. The host has already logged it with its stack trace.
    Console.Error.WriteLine($"{SourceLabels.ConsolePrefix} host failed: {ex.Message}");
    return 1;
}

return 0;

static StandInOptions BindStandInOptions(IConfiguration configuration) =>
    configuration.GetSection(StandInOptions.SectionName).Get<StandInOptions>() ?? new StandInOptions();

static IPAddress ParseBindAddress(string? value) =>
    IPAddress.TryParse(value, out IPAddress? address)
        ? address
        : throw new InvalidOperationException(
            $"{StandInOptions.SectionName}:{nameof(StandInOptions.BindAddress)} must be an IP address; the value is '{value}'.");

// Kestrel options are built while the app is built, before the listener-port validation registered by
// AddListenerPortMatcherPolicy runs, so an out-of-range port is reported here under its own key. Port 0
// is rejected too: the matcher policy needs the exact port each listener is bound to.
static int ListenerPort(int port, string key) =>
    port is >= 1 and <= IPEndPoint.MaxPort
        ? port
        : throw new InvalidOperationException(
            $"{StandInOptions.SectionName}:{key} must be between 1 and {IPEndPoint.MaxPort}; the value is {port}.");

// Reads the raw setting, so describing a failure never re-runs the options binding that may have caused it.
static string DescribeConfiguredSeedDirectory(IConfiguration configuration)
{
    string configured = configuration[$"{StandInOptions.SectionName}:{nameof(StandInOptions.SeedDirectory)}"]
        ?? new StandInOptions().SeedDirectory;
    return string.IsNullOrWhiteSpace(configured) ? "(not configured)" : Path.GetFullPath(configured);
}
