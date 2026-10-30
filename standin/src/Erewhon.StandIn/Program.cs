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
using Erewhon.StandIn;
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

// Printed before anything can fail, so a start-up failure is labelled as stand-in output too.
Console.WriteLine(SourceLabels.Banner);

// One boundary covers everything from reading configuration to mapping the endpoints. A configuration
// file that does not parse, a setting that does not convert, a bind address that does not parse, a
// missing seed directory, an invalid reference date or a failed registration or mapping therefore
// stops the process with one line on standard error and exit code 1, instead of an
// unhandled-exception abort or a listener answering from an empty model. Build (which applies the
// Kestrel settings) and the first seed load run before any endpoint is mapped or served, so after the
// load /admin/info reports generation 1 and mutations 0.
IConfiguration? configuration = null;
StandInState? state = null;
WebApplication app;
try
{
    // The content root is the build output, so appsettings.json loads whatever the working directory is.
    // StandIn:SeedDirectory is deliberately not resolved against it: StandInState resolves it against the
    // working directory (standin/ locally, an absolute /app/seed in the image).
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });
    configuration = builder.Configuration;

    // Framework request logs print raw paths, which carry names; held at Warning whatever Logging says.
    builder.Logging
        .AddLogLevelFloor("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning)
        .AddLogLevelFloor("Microsoft.AspNetCore.Routing", LogLevel.Warning)
        .AddLogLevelFloor("Microsoft.AspNetCore.Server.Kestrel.BadRequests", LogLevel.Warning);

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

    app = builder.Build();
    state = app.Services.GetRequiredService<StandInState>();
    state.Reset(ResetKind.Seed);

    RequestLogging.Use(app);
    RestEndpoints.Map(app);
    SoapEndpoint.Map(app);
    AdminEndpoints.Map(app);
}
catch (Exception ex)
{
    Console.Error.WriteLine(
        $"{SourceLabels.ConsolePrefix} start-up failed ({DescribeSeedDirectory(configuration, state)}): {EscapeControlCharacters(ex.Message)}");
    return 1;
}

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

// Names the StandIn:SeedDirectory value a start-up failure concerns, and never throws, so the failure
// itself is always reported. Once the state exists it is the state's directory. Before that, the raw
// setting is resolved exactly as the state resolves it, without re-running the options binding that
// may have caused the failure; a value that does not resolve is shown as configured. Without
// configuration (the builder itself failed) no value is known.
static string DescribeSeedDirectory(IConfiguration? configuration, StandInState? state)
{
    const string Key = StandInOptions.SectionName + ":" + nameof(StandInOptions.SeedDirectory);
    if (state != null)
    {
        return $"{Key} '{EscapeControlCharacters(state.SeedDirectory)}'";
    }

    if (configuration == null)
    {
        return $"{Key} unknown, configuration not loaded";
    }

    string? configured = null;
    try
    {
        configured = configuration[Key] ?? new StandInOptions().SeedDirectory;
        return $"{Key} '{EscapeControlCharacters(StandInState.ResolveSeedDirectory(configured))}'";
    }
    catch (Exception)
    {
        return configured == null
            ? $"{Key} unreadable"
            : $"{Key} '{EscapeControlCharacters(configured)}', unresolved";
    }
}

// Writes each control character (U+0000 to U+001F, U+007F to U+009F) and each line or paragraph
// separator (U+2028, U+2029) as \uXXXX, so a value from configuration or an exception message keeps
// the start-up failure report on one printable line.
static string EscapeControlCharacters(string value)
{
    var escaped = new StringBuilder(value.Length);
    foreach (char c in value)
    {
        if (char.IsControl(c) || c is '\u2028' or '\u2029')
        {
            escaped.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        }
        else
        {
            escaped.Append(c);
        }
    }

    return escaped.ToString();
}

namespace Erewhon.StandIn
{
    using System.Diagnostics;

    /// <summary>How a request on a stand-in listener ended, as its request log line reports it.</summary>
    public enum RequestOutcome
    {
        /// <summary>The handler returned normally; the status is the one it set.</summary>
        Completed,

        /// <summary>The client went away first, so the request could not be read or answered in full.</summary>
        Aborted,

        /// <summary>An exception other than a client abort ended the handler, which the stand-in does not expect.</summary>
        Failed,
    }

    /// <summary>
    /// The facts the REST and SOAP handlers record about the request they answer, for its line in
    /// <see cref="RequestLogging"/>.
    /// </summary>
    /// <remarks>
    /// Only stand-in constants and numbers are recorded: the catalogue operation id, the stand-in's fixed
    /// Fault string and a <c>DTOResult</c> status code. The request's path, query, headers, body and
    /// parameter values never are, because REST path segments and SOAP bodies carry agent and client names.
    /// </remarks>
    public sealed class RequestLogEntry
    {
        /// <summary>The operation label of a request that resolved to no catalogue operation.</summary>
        public const string Unmatched = "unmatched";

        /// <summary>The catalogue operation id, for example <c>Rest.getAgent</c>, or <see cref="Unmatched"/>.</summary>
        public string Operation { get; set; } = Unmatched;

        /// <summary>The <c>faultstring</c> of the SOAP Fault the stand-in wrote; null when it wrote none.</summary>
        public string? FaultString { get; set; }

        /// <summary>The <c>statusCode</c> of the SOAP <c>DTOResult</c> the stand-in wrote; null when the result has none.</summary>
        public int? ResultStatusCode { get; set; }

        /// <summary>How the handler ended; <see cref="RequestOutcome.Completed"/> unless it records otherwise.</summary>
        public RequestOutcome Outcome { get; set; } = RequestOutcome.Completed;

        /// <summary>
        /// Returns the entry attached to <paramref name="context"/>, attaching a new one when there is none,
        /// so a handler can always record its facts; they are written where <see cref="RequestLogging"/> runs.
        /// </summary>
        /// <param name="context">The request.</param>
        /// <returns>The request's entry.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
        public static RequestLogEntry For(HttpContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.Features.Get<RequestLogEntry>() is { } entry)
            {
                return entry;
            }

            entry = new RequestLogEntry();
            context.Features.Set(entry);
            return entry;
        }
    }

    /// <summary>
    /// Writes one structured console line per request on every stand-in listener, so the log accounts for
    /// each request without recording anything a caller sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A line names the listener, the operation, the HTTP status, the outcome, the duration and the reset
    /// generation current when the request ended, plus the Fault string or the <c>DTOResult</c> status code
    /// a SOAP handler recorded. REST and SOAP lines are Information up to 4xx, Warning for the stand-in's
    /// own 5xx answers (REST 500 with null, SOAP Faults, 501) and Error for an unexpected exception. Admin
    /// lines are Debug, so health polling does not flood the default log, which keeps the reset and
    /// shutdown lines of the admin endpoints. Framework request logging is held at Warning whatever the
    /// Logging configuration says, because its lines print the raw path, and REST path segments carry names.
    /// </para>
    /// <para>
    /// The line is operational logging, not an audit record: it commits and stores nothing. It changes no
    /// response: no header, status or body is touched, and the duration is measured only for the log. An
    /// exception that leaves a handler is logged and rethrown unchanged, so the host's handling of it, and
    /// the response sent for it, are not altered.
    /// </para>
    /// </remarks>
    public static partial class RequestLogging
    {
        /// <summary>Adds the request log middleware to the stand-in's pipeline.</summary>
        /// <param name="app">The stand-in's application builder, after the state has been loaded.</param>
        /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
        /// <exception cref="InvalidOperationException">The logger factory, the state or the listener ports are not registered.</exception>
        public static void Use(IApplicationBuilder app)
        {
            ArgumentNullException.ThrowIfNull(app);

            IServiceProvider services = app.ApplicationServices;
            ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RequestLogging));
            StandInState state = services.GetRequiredService<StandInState>();

            // Value is read per request, not here: the ports are validated when the host starts, and
            // reading them earlier would move that failure out of the host's start-up report.
            IOptions<ListenerPorts> ports = services.GetRequiredService<IOptions<ListenerPorts>>();

            app.Use((context, next) => LogRequestAsync(context, next, logger, state, ports));
        }

        private static async Task LogRequestAsync(
            HttpContext context,
            RequestDelegate next,
            ILogger logger,
            StandInState state,
            IOptions<ListenerPorts> ports)
        {
            long started = Stopwatch.GetTimestamp();

            // Always a fresh entry, so no fact can carry over between requests on one connection.
            var entry = new RequestLogEntry();
            context.Features.Set(entry);
            Exception? escaped = null;
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                escaped = exception;
                throw;
            }
            finally
            {
                Write(context, entry, escaped, Stopwatch.GetElapsedTime(started), logger, state, ports.Value);
            }
        }

        private static void Write(
            HttpContext context,
            RequestLogEntry entry,
            Exception? escaped,
            TimeSpan elapsed,
            ILogger logger,
            StandInState state,
            ListenerPorts ports)
        {
            int localPort = context.Connection.LocalPort;
            bool admin = localPort == ports.AdminPort;

            // An exception that left the handler is an abort under the same rule the handlers apply;
            // anything else is a defect.
            RequestOutcome outcome = escaped is null
                ? entry.Outcome
                : context.RequestAborted.IsCancellationRequested && escaped is OperationCanceledException or IOException
                    ? RequestOutcome.Aborted
                    : RequestOutcome.Failed;
            int status = StatusOf(context.Response, escaped, outcome);

            LogLevel level = admin
                ? LogLevel.Debug
                : outcome == RequestOutcome.Failed
                    ? LogLevel.Error
                    : status >= StatusCodes.Status500InternalServerError ? LogLevel.Warning : LogLevel.Information;
            if (!logger.IsEnabled(level))
            {
                return;
            }

            string listener = ListenerName(localPort, ports);
            string operation = admin ? AdminOperation(context) : entry.Operation;
            string outcomeText = OutcomeText(outcome);
            double durationMs = Math.Round(elapsed.TotalMilliseconds, 3);
            long generation = state.Generation;

            if (entry.FaultString is { } faultString)
            {
                LogFaultRequest(logger, level, listener, operation, status, faultString, outcomeText, durationMs, generation);
            }
            else if (entry.ResultStatusCode is { } resultStatusCode)
            {
                LogResultRequest(logger, level, listener, operation, status, resultStatusCode, outcomeText, durationMs, generation);
            }
            else
            {
                LogRequest(logger, level, listener, operation, status, outcomeText, durationMs, generation);
            }
        }

        // The status the host sends for an exception that left the handler before the response started: a
        // BadHttpRequestException's own status, otherwise 500. Once the response has started, or the
        // client has gone, it is the status the handler set.
        private static int StatusOf(HttpResponse response, Exception? escaped, RequestOutcome outcome) =>
            escaped is null || outcome == RequestOutcome.Aborted || response.HasStarted
                ? response.StatusCode
                : escaped is BadHttpRequestException badRequest
                    ? badRequest.StatusCode
                    : StatusCodes.Status500InternalServerError;

        // The listener whose socket the request arrived on, by the rule the listener matcher policy applies.
        // Kestrel listens on exactly these three ports, so "unknown" marks a host misconfiguration.
        private static string ListenerName(int localPort, ListenerPorts ports) =>
            localPort == ports.RestPort ? "rest"
            : localPort == ports.SoapPort ? "soap"
            : localPort == ports.AdminPort ? "admin"
            : "unknown";

        // An admin endpoint is named by its fixed route, for example /admin/reset. The catch-all's route
        // captures the caller's path, so it is named unmatched instead.
        private static string AdminOperation(HttpContext context) =>
            context.GetEndpoint() is RouteEndpoint { RoutePattern: { Parameters.Count: 0, RawText: { } route } }
                ? route
                : RequestLogEntry.Unmatched;

        private static string OutcomeText(RequestOutcome outcome) => outcome switch
        {
            RequestOutcome.Completed => "completed",
            RequestOutcome.Aborted => "aborted",
            RequestOutcome.Failed => "failed",
            _ => ((int)outcome).ToString(CultureInfo.InvariantCulture),
        };

        [LoggerMessage(EventId = 1, Message = "request listener={Listener} operation={Operation} status={Status} outcome={Outcome} durationMs={DurationMs} generation={Generation}")]
        private static partial void LogRequest(
            ILogger logger, LogLevel level, string listener, string operation, int status, string outcome, double durationMs, long generation);

        [LoggerMessage(EventId = 2, Message = "request listener={Listener} operation={Operation} status={Status} fault='{FaultString}' outcome={Outcome} durationMs={DurationMs} generation={Generation}")]
        private static partial void LogFaultRequest(
            ILogger logger, LogLevel level, string listener, string operation, int status, string faultString, string outcome, double durationMs, long generation);

        [LoggerMessage(EventId = 3, Message = "request listener={Listener} operation={Operation} status={Status} resultStatusCode={ResultStatusCode} outcome={Outcome} durationMs={DurationMs} generation={Generation}")]
        private static partial void LogResultRequest(
            ILogger logger, LogLevel level, string listener, string operation, int status, int resultStatusCode, string outcome, double durationMs, long generation);
    }
}
