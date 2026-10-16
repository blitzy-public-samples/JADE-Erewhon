using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoreWCF;
using CoreWCF.Channels;
using CoreWCF.Configuration;
using CoreWCF.Description;
using Erewhon.Api.App;
using Erewhon.Api.Cli;
using Erewhon.Api.Closure;
using Erewhon.Api.Errors;
using Erewhon.Api.Realtime;
using Erewhon.Api.Rest;
using Erewhon.Api.Sessions;
using Erewhon.Api.Soap;
using Erewhon.Application;
using Erewhon.Application.Abstractions;
using Erewhon.Persistence;
using HealthChecks.NpgSql;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Compact;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Erewhon.Api;

/// <summary>
/// Builds and runs the Erewhon web host: Serilog logging, the Application and Persistence layers, the REST
/// controllers under <c>/api/rest</c>, the app endpoints under <c>/api/app</c>, the SOAP services
/// <c>/soap/ErewhonWebService</c> and <c>/soap/ErewhonWebServiceAdmin</c> with their WSDL, the SignalR hub
/// <c>/hubs/erewhon</c>, the tender-closure schedule service, the OpenAPI documents <c>rest</c> and <c>app</c> with
/// the UI at <c>/swagger</c>, the PostgreSQL health check at <c>/healthz</c>, and the React client served from
/// <c>wwwroot</c>; or runs the <c>migrate</c> and <c>seed</c> command-line verbs. Ports
/// <c>ErewhonModelSchemaApp.initialize</c>, the <c>ErewhonREST</c>, <c>WebServiceHTTP</c> and <c>WebServiceTCP</c>
/// applications and the <c>DataLoader</c> application start-up.
/// </summary>
public static class ErewhonHost
{
    private const string ConnectionStringName = "Erewhon";
    private const string HealthCheckName = "postgresql";
    private const string HealthCheckPath = "/healthz";
    private const string AppGroupPath = "/api/app";
    private const string RestDocumentName = "rest";
    private const string AppDocumentName = "app";
    private const string DocumentVersion = "v1";
    private const string WebServicePath = "/soap/ErewhonWebService";
    private const string WebServiceAdminPath = "/soap/ErewhonWebServiceAdmin";
    private const string SpaFallbackFile = "index.html";
    private const string ReservedFallbackPattern = "{**path:nonfile}";
    private static readonly string[] ReservedPathPrefixes = ["/api", "/soap", "/hubs", "/swagger", HealthCheckPath];
    private const int ShutdownExitCode = 0;

    /// <summary>The exit code of a command-line verb that ends because its cancellation token was cancelled.</summary>
    private const int CancelledVerbExitCode = 1;

    /// <summary>The exit code of a process whose host failed to be built, started, run or disposed.</summary>
    private const int HostFailureExitCode = 1;

    /// <summary>The category of the event that <see cref="LogHostFailure"/> writes.</summary>
    private const string HostLoggerCategory = "Erewhon.Api.ErewhonHost";

    /// <summary>The message template of the event that <see cref="LogHostFailure"/> writes.</summary>
    private const string HostFailureTemplate = "The Erewhon host failed; the process exits with code {ExitCode}";

    /// <summary>The event id of the event that <see cref="LogHostFailure"/> writes: 1 <c>ErewhonHostFailed</c>.</summary>
    private static readonly EventId HostFailureEventId = new(1, "ErewhonHostFailed");

    /// <summary>
    /// Creates the web application builder from <paramref name="args"/>, with the content root
    /// <see cref="FindProjectContentRoot"/> returns or, when it returns none, the framework's: the configured
    /// <c>contentRoot</c>, or the current directory. Passes the builder to <paramref name="configure"/>
    /// before any service is registered, registers every Erewhon service, and builds the application with its whole
    /// request pipeline and every endpoint mapped. Once the application is built, and before the pipeline is mapped,
    /// <see cref="EntityLockSettings"/> is resolved, which logs each rejected lock setting as
    /// <c>AddErewhonPersistence</c> describes. The returned application has not been started.
    /// <list type="bullet">
    /// <item><description>
    /// Services: Serilog from the <c>Serilog</c> configuration section plus every Serilog sink, enricher and filter
    /// registered in dependency injection, plus the console sink and levels of <see cref="AddFallbackLogging"/> when
    /// that section has no <c>WriteTo</c> entry, leaving the static <see cref="Log.Logger"/> unchanged;
    /// <c>AddErewhonApplication</c> and <c>AddErewhonPersistence</c>; the HTTP context accessor; a singleton
    /// <see cref="SessionRegistry"/>; a scoped <see cref="HttpSessionContext"/> as <see cref="ISessionContext"/>; a
    /// singleton <see cref="SignalRNotificationPublisher"/> as <see cref="INotificationPublisher"/>; a singleton
    /// <see cref="ClosureScheduleRegistry"/> and the hosted <see cref="TenderClosureScheduleService"/>; transient
    /// <see cref="ErewhonWebService"/> and <see cref="ErewhonWebServiceAdmin"/>; the controllers of this assembly with
    /// the global <see cref="RestExceptionFilter"/> and no implicit required attribute on non-nullable reference
    /// types; CoreWCF with metadata addresses taken from the request headers; SignalR; Swashbuckle with the
    /// documents <c>rest</c> and <c>app</c>; the PostgreSQL health check <c>postgresql</c>, which reads
    /// <c>ConnectionStrings:Erewhon</c> each time it runs, opens its own unpooled connection with it and closes it
    /// after <c>SELECT 1</c>, and reports <c>Unhealthy</c> when the value is absent or empty, or when the connection
    /// cannot be opened or <c>SELECT 1</c> fails, with a description that names only that setting, the exception
    /// types and the SQLSTATE, and with no exception or data.
    /// </description></item>
    /// <item><description>
    /// JSON for controllers, minimal APIs and SignalR: camelCase property names, and enums written as camelCase
    /// strings.
    /// </description></item>
    /// <item><description>
    /// Minimal APIs in every environment: a request whose parameters cannot be bound is answered with the framework's
    /// status, 400 for a malformed or wrongly typed JSON body, and an empty body, without throwing.
    /// </description></item>
    /// <item><description>
    /// Pipeline, in order: Serilog request logging through the host's logger; <see cref="ErrorOutcomeMiddleware"/>;
    /// static files; the OpenAPI
    /// documents at <c>/swagger/{document}/swagger.json</c> and the UI at <c>/swagger</c>;
    /// <see cref="SessionMiddleware"/>; the two SOAP endpoints on SOAP 1.1 <c>BasicHttpBinding</c> with WSDL at
    /// <c>?wsdl</c>; the REST controllers; the app endpoint groups under <c>/api/app</c>; the hub at
    /// <c>/hubs/erewhon</c>; the health check at <c>/healthz</c>, answering <c>Healthy</c> with 200 or
    /// <c>Unhealthy</c> with 503; 404 with an empty body for every other path without a file extension whose first
    /// segment is <c>api</c>, <c>soap</c>, <c>hubs</c>, <c>swagger</c> or <c>healthz</c>, compared case-insensitively;
    /// and <c>index.html</c> for every other path without a file extension.
    /// </description></item>
    /// </list>
    /// </summary>
    /// <param name="args">The arguments the builder reads as command-line configuration.</param>
    /// <param name="configure">
    /// Optional callback that receives the builder immediately after it is created, before any Erewhon registration
    /// reads its configuration or services.
    /// </param>
    /// <returns>The built, unstarted web application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = FindProjectContentRoot(args),
        });
        configure?.Invoke(builder);

        AddServices(builder);

        var app = builder.Build();
        _ = app.Services.GetRequiredService<EntityLockSettings>();
        MapPipeline(app);
        return app;
    }

    /// <summary>
    /// Returns the content root of a host that runs from the build output of its project: the nearest folder at or
    /// above <see cref="AppContext.BaseDirectory"/> that holds the project file named after this assembly,
    /// <c>Erewhon.Api.csproj</c>.
    /// </summary>
    /// <param name="args">The arguments the builder reads as command-line configuration.</param>
    /// <returns>
    /// The full path of that folder; <see langword="null"/> when no such folder exists, when <paramref name="args"/>
    /// gives a <c>contentRoot</c> value, read as command-line configuration, or when the environment variable
    /// <c>ASPNETCORE_CONTENTROOT</c> or <c>DOTNET_CONTENTROOT</c> is set, even to an empty value.
    /// </returns>
    private static string? FindProjectContentRoot(string[] args)
    {
        const string AspNetCoreContentRootVariable = "ASPNETCORE_CONTENTROOT";
        const string DotNetContentRootVariable = "DOTNET_CONTENTROOT";
        const string ProjectFileExtension = ".csproj";

        var configuredContentRoot = new ConfigurationBuilder().AddCommandLine(args).Build()[HostDefaults.ContentRootKey];
        if (configuredContentRoot is not null
            || Environment.GetEnvironmentVariable(AspNetCoreContentRootVariable) is not null
            || Environment.GetEnvironmentVariable(DotNetContentRootVariable) is not null)
        {
            return null;
        }

        var projectFileName = typeof(ErewhonHost).Assembly.GetName().Name + ProjectFileExtension;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, projectFileName)))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    /// <summary>
    /// Runs the process: when <see cref="CommandLineVerbs.IsVerb"/> recognises <paramref name="args"/>, builds the
    /// host without command-line configuration and runs the verb through <see cref="RunVerbAsync"/> without starting
    /// the web server or the hosted services; otherwise builds the host from <paramref name="args"/> and runs the web
    /// server through <see cref="RunWebServerAsync"/> until it shuts down. The host is disposed afterwards.
    /// An exception thrown while the host is built, started, run, stopped or disposed is not rethrown:
    /// <see cref="LogHostFailure"/> writes it as one fatal event, through the host's logger when the host was built
    /// and is not being disposed, and the process ends with exit code 1.
    /// </summary>
    /// <param name="args">The process arguments.</param>
    /// <returns>
    /// The verb's exit code; 1 when the verb ends with an <see cref="OperationCanceledException"/> after its token was
    /// cancelled; 0 once the web server has shut down; or 1 when building, starting, running, stopping or disposing
    /// the host threw an exception.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var isVerb = CommandLineVerbs.IsVerb(args);
        WebApplication? host = null;
        int exitCode;
        try
        {
            host = Build(isVerb ? Array.Empty<string>() : args);
            exitCode = isVerb ? await RunVerbAsync(host, args) : await RunWebServerAsync(host);
        }
        catch (Exception failure)
        {
            LogHostFailure(host?.Services, failure);
            exitCode = HostFailureExitCode;
        }

        if (host is null)
        {
            return exitCode;
        }

        try
        {
            await host.DisposeAsync();
        }
        catch (Exception failure)
        {
            LogHostFailure(null, failure);
            exitCode = HostFailureExitCode;
        }

        return exitCode;
    }

    /// <summary>
    /// Runs the command-line verb of <paramref name="args"/> through <see cref="CommandLineVerbs.RunAsync"/> with the
    /// services of the unstarted <paramref name="host"/>, writing its output to standard output.
    /// While the verb runs, SIGINT, SIGTERM and SIGQUIT are handled by <see cref="CancelVerb"/>: the first of them
    /// cancels the token passed to the verb and the process keeps running until the verb ends; a signal received after
    /// that gets the runtime's default handling, which ends the process. The signal registrations and the token's
    /// source are disposed when the verb ends.
    /// </summary>
    /// <param name="host">The built host whose services run the verb.</param>
    /// <param name="args">The process arguments; the first is the verb.</param>
    /// <returns>
    /// The verb's exit code, or 1 when the verb ends with an <see cref="OperationCanceledException"/> after its token
    /// was cancelled.
    /// </returns>
    private static async Task<int> RunVerbAsync(WebApplication host, string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Action<PosixSignalContext> onSignal = context => CancelVerb(context, cancellation);
        using var interruptRegistration = PosixSignalRegistration.Create(PosixSignal.SIGINT, onSignal);
        using var terminateRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, onSignal);
        using var quitRegistration = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, onSignal);
        try
        {
            return await CommandLineVerbs.RunAsync(host.Services, args, Console.Out, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return CancelledVerbExitCode;
        }
    }

    /// <summary>
    /// Starts <paramref name="app"/> and waits until it has shut down and its hosted services have stopped, without
    /// disposing it. A start that ends with an <see cref="OperationCanceledException"/> after the application's
    /// shutdown was requested, by Ctrl+C or SIGTERM while it starts, ends the run as a shutdown.
    /// </summary>
    /// <param name="app">The built, unstarted web application.</param>
    /// <returns>0 once the application has shut down.</returns>
    private static async Task<int> RunWebServerAsync(WebApplication app)
    {
        try
        {
            await app.StartAsync();
        }
        catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
        {
            return ShutdownExitCode;
        }

        await app.WaitForShutdownAsync();
        return ShutdownExitCode;
    }

    /// <summary>
    /// Writes one <c>Critical</c> (Serilog <c>Fatal</c>) event with <see cref="HostFailureEventId"/> under the category
    /// <c>Erewhon.Api.ErewhonHost</c>, with <paramref name="failure"/> attached and the exit code 1 as its
    /// <c>ExitCode</c> property. The event goes through the logger factory of <paramref name="services"/> when it is
    /// given; otherwise through a logger created for the event, which writes it as one compact JSON line to standard
    /// output and is disposed after it.
    /// </summary>
    /// <param name="services">
    /// The services of the built host while it is not being disposed, or <see langword="null"/>.
    /// </param>
    /// <param name="failure">The exception that ended the host.</param>
    private static void LogHostFailure(IServiceProvider? services, Exception failure)
    {
        if (services is not null)
        {
            WriteHostFailure(services.GetRequiredService<ILoggerFactory>(), failure);
            return;
        }

        using var loggerFactory = new SerilogLoggerFactory(
            new LoggerConfiguration().WriteTo.Console(new CompactJsonFormatter()).CreateLogger(),
            dispose: true);
        WriteHostFailure(loggerFactory, failure);
    }

    /// <summary>Writes the event of <see cref="LogHostFailure"/> through <paramref name="loggerFactory"/>.</summary>
    /// <param name="loggerFactory">The factory of the logger that writes the event.</param>
    /// <param name="failure">The exception that ended the host.</param>
    private static void WriteHostFailure(ILoggerFactory loggerFactory, Exception failure) =>
        loggerFactory
            .CreateLogger(HostLoggerCategory)
            .LogCritical(HostFailureEventId, failure, HostFailureTemplate, HostFailureExitCode);

    /// <summary>
    /// Handles a SIGINT, SIGTERM or SIGQUIT received while a command-line verb runs. When
    /// <paramref name="cancellation"/> has no cancellation requested yet, requests it, with the token's callbacks run
    /// on the thread pool, and marks the signal handled so that the process keeps running. When cancellation has
    /// already been requested, or <paramref name="cancellation"/> has been disposed, leaves the signal to the runtime's
    /// default handling, which ends the process.
    /// </summary>
    /// <param name="context">The context of the received signal.</param>
    /// <param name="cancellation">The source of the token passed to the running verb.</param>
    private static void CancelVerb(PosixSignalContext context, CancellationTokenSource cancellation)
    {
        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            _ = cancellation.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        context.Cancel = true;
    }

    /// <summary>
    /// When <paramref name="settings"/> holds no entry under <c>Serilog:WriteTo</c>, adds to
    /// <paramref name="configuration"/> a console sink that writes each event as one compact JSON line to standard
    /// output, and the minimum level <c>Warning</c> for each of the sources <c>Microsoft.AspNetCore</c>,
    /// <c>Microsoft.AspNetCore.Hosting.Diagnostics</c> and <c>Microsoft.EntityFrameworkCore</c> that has no
    /// <c>Serilog:MinimumLevel:Override</c> value in <paramref name="settings"/>. Otherwise leaves
    /// <paramref name="configuration"/> unchanged.
    /// </summary>
    /// <param name="configuration">The logger configuration that has read <paramref name="settings"/>.</param>
    /// <param name="settings">The host configuration.</param>
    private static void AddFallbackLogging(LoggerConfiguration configuration, IConfiguration settings)
    {
        const string WriteToSection = "Serilog:WriteTo";
        const string OverrideSection = "Serilog:MinimumLevel:Override";
        string[] warningSources =
            ["Microsoft.AspNetCore", "Microsoft.AspNetCore.Hosting.Diagnostics", "Microsoft.EntityFrameworkCore"];

        if (settings.GetSection(WriteToSection).GetChildren().Any())
        {
            return;
        }

        configuration.WriteTo.Console(new CompactJsonFormatter());
        foreach (var source in warningSources)
        {
            if (settings[$"{OverrideSection}:{source}"] is null)
            {
                configuration.MinimumLevel.Override(source, LogEventLevel.Warning);
            }
        }
    }

    /// <summary>Registers the logging, layer, session, realtime, closure, SOAP, REST, OpenAPI and health services.</summary>
    /// <param name="builder">The builder whose services are registered.</param>
    private static void AddServices(WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog(
            (context, services, configuration) => AddFallbackLogging(
                configuration
                    .ReadFrom.Configuration(context.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext(),
                context.Configuration),
            preserveStaticLogger: true);

        var services = builder.Services;

        services.AddErewhonApplication();
        services.AddErewhonPersistence(builder.Configuration);

        services.AddHttpContextAccessor();
        services.AddSingleton<SessionRegistry>();
        services.AddScoped<ISessionContext, HttpSessionContext>();
        services.AddSingleton<INotificationPublisher, SignalRNotificationPublisher>();
        services.AddSingleton<ClosureScheduleRegistry>();
        services.AddHostedService<TenderClosureScheduleService>();
        services.AddTransient<ErewhonWebService>();
        services.AddTransient<ErewhonWebServiceAdmin>();

        services.ConfigureHttpJsonOptions(options => ConfigureJson(options.SerializerOptions));
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
        services
            .AddControllers(options =>
            {
                options.Filters.Add<RestExceptionFilter>();
                options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
            })
            .AddApplicationPart(typeof(ErewhonHost).Assembly)
            .AddJsonOptions(options => ConfigureJson(options.JsonSerializerOptions));

        services.AddServiceModelServices();
        services.AddServiceModelMetadata();
        services.AddSingleton<IServiceBehavior, UseRequestHeadersForMetadataAddressBehavior>();

        services
            .AddSignalR()
            .AddJsonProtocol(options => ConfigureJson(options.PayloadSerializerOptions));

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc(RestDocumentName, new OpenApiInfo { Title = "Erewhon REST API", Version = DocumentVersion });
            options.SwaggerDoc(AppDocumentName, new OpenApiInfo { Title = "Erewhon app API", Version = DocumentVersion });
            options.DocInclusionPredicate((documentName, description) => description.GroupName == documentName);

            var xmlDocumentation = Path.Combine(
                AppContext.BaseDirectory,
                $"{typeof(ErewhonHost).Assembly.GetName().Name}.xml");
            if (File.Exists(xmlDocumentation))
            {
                options.IncludeXmlComments(xmlDocumentation);
            }

            options.CustomSchemaIds(type => (type.FullName ?? type.Name).Replace('+', '.'));
            options.UseAllOfForInheritance();
            options.UseOneOfForPolymorphism();

            var assemblySubTypes = options.SchemaGeneratorOptions.SubTypesSelector;
            options.SelectSubTypesUsing(
                type => JsonPolymorphismSchemaFilter.DerivedTypes(type) ?? assemblySubTypes(type));
            options.SelectDiscriminatorNameUsing(JsonPolymorphismSchemaFilter.DiscriminatorName);
            options.SelectDiscriminatorValueUsing(JsonPolymorphismSchemaFilter.DiscriminatorValue);
            options.SchemaFilter<JsonPolymorphismSchemaFilter>();
        });

        services
            .AddHealthChecks()
            .Add(new HealthCheckRegistration(
                HealthCheckName,
                provider => CreateDatabaseHealthCheck(provider.GetRequiredService<IConfiguration>()),
                failureStatus: null,
                tags: null));
    }

    /// <summary>
    /// Creates the check of one health-check run from the current value of <c>ConnectionStrings:Erewhon</c>.
    /// </summary>
    /// <param name="configuration">The configuration the connection string is read from.</param>
    /// <returns>
    /// A <see cref="MissingConnectionStringHealthCheck"/> when the value is absent or empty; otherwise a
    /// <see cref="SanitizedDatabaseHealthCheck"/> that, each time it runs, creates an <see cref="NpgSqlHealthCheck"/>
    /// that opens an unpooled connection with the <see cref="UnpooledConnectionString"/> of the value, runs
    /// <c>SELECT 1</c> and closes the connection.
    /// </returns>
    private static IHealthCheck CreateDatabaseHealthCheck(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        return string.IsNullOrEmpty(connectionString)
            ? new MissingConnectionStringHealthCheck()
            : new SanitizedDatabaseHealthCheck(() => new NpgSqlHealthCheck(
                new NpgSqlHealthCheckOptions(UnpooledConnectionString(connectionString))));
    }

    /// <summary>
    /// Returns <paramref name="connectionString"/> with <c>Pooling</c> and <c>Multiplexing</c> set to
    /// <see langword="false"/> and every other setting unchanged. A connection opened with the result opens its own
    /// physical connection to PostgreSQL, never one held by an Npgsql pool, and closes it when the connection closes.
    /// </summary>
    /// <param name="connectionString">The connection string to copy.</param>
    /// <returns>The unpooled copy of <paramref name="connectionString"/>.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="connectionString"/> is not a valid Npgsql connection string.
    /// </exception>
    private static string UnpooledConnectionString(string connectionString) =>
        new Npgsql.NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, Multiplexing = false }
            .ConnectionString;

    /// <summary>Adds the middleware and maps every endpoint of the Erewhon host, in pipeline order.</summary>
    /// <param name="app">The built application.</param>
    private static void MapPipeline(WebApplication app)
    {
        app.UseSerilogRequestLogging(options => options.Logger = app.Services.GetRequiredService<Serilog.ILogger>());
        app.UseMiddleware<ErrorOutcomeMiddleware>();
        app.UseStaticFiles();

        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint($"/swagger/{RestDocumentName}/swagger.json", RestDocumentName);
            options.SwaggerEndpoint($"/swagger/{AppDocumentName}/swagger.json", AppDocumentName);
        });

        app.UseMiddleware<SessionMiddleware>();

        app.UseServiceModel(serviceBuilder =>
        {
            serviceBuilder.AddService<ErewhonWebService>();
            serviceBuilder.AddServiceEndpoint<ErewhonWebService, IErewhonWebService>(
                new BasicHttpBinding(BasicHttpSecurityMode.None),
                WebServicePath);
            serviceBuilder.AddService<ErewhonWebServiceAdmin>();
            serviceBuilder.AddServiceEndpoint<ErewhonWebServiceAdmin, IErewhonWebServiceAdmin>(
                new BasicHttpBinding(BasicHttpSecurityMode.None),
                WebServiceAdminPath);
        });
        app.Services.GetRequiredService<ServiceMetadataBehavior>().HttpGetEnabled = true;

        app.MapControllers();

        var api = app.MapGroup(AppGroupPath).WithGroupName(AppDocumentName);
        api.MapSessionEndpoints();
        api.MapSetupEndpoints();
        api.MapShopEndpoints();
        api.MapAdminPartyEndpoints();
        api.MapAdminReferenceEndpoints();
        api.MapAdminItemEndpoints();
        api.MapAdminTenderEndpoints();
        api.MapAdminSalesEndpoints();
        api.MapClosureEndpoints();
        api.MapDocumentationEndpoints();
        api.MapEditLockEndpoints();
        api.MapAboutEndpoints();

        app.MapHub<ErewhonHub>(ErewhonHub.Path);
        app.MapHealthChecks(HealthCheckPath);
        foreach (var prefix in ReservedPathPrefixes)
        {
            app.MapFallback($"{prefix}/{ReservedFallbackPattern}", static context =>
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            });
        }

        app.MapFallbackToFile(SpaFallbackFile);
    }

    /// <summary>Sets camelCase property names and adds the camelCase string enum converter.</summary>
    /// <param name="options">The serializer options to configure.</param>
    private static void ConfigureJson(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    /// <summary>
    /// Describes to the OpenAPI schema generator the System.Text.Json polymorphism that
    /// <see cref="JsonDerivedTypeAttribute"/> and <see cref="JsonPolymorphicAttribute"/> declare: the derived types of
    /// a base type, its discriminator property name and the discriminator value of each derived type; as a schema
    /// filter, leaves the discriminator property of a schema optional.
    /// </summary>
    private sealed class JsonPolymorphismSchemaFilter : ISchemaFilter
    {
        /// <summary>
        /// The discriminator property name System.Text.Json writes when none is declared: <c>$type</c>.
        /// </summary>
        private static readonly string DefaultDiscriminatorName =
            new System.Text.Json.Serialization.Metadata.JsonPolymorphismOptions().TypeDiscriminatorPropertyName;

        /// <summary>Returns the derived types that the attributes of <paramref name="baseType"/> declare.</summary>
        /// <param name="baseType">The type whose <see cref="JsonDerivedTypeAttribute"/> attributes are read.</param>
        /// <returns>
        /// The declared derived types, or <see langword="null"/> when <paramref name="baseType"/> declares none.
        /// </returns>
        public static IEnumerable<Type>? DerivedTypes(Type baseType)
        {
            var derivedTypes = DerivedTypeAttributes(baseType).Select(attribute => attribute.DerivedType).ToArray();
            return derivedTypes.Length == 0 ? null : derivedTypes;
        }

        /// <summary>
        /// Returns the discriminator property name System.Text.Json writes for the derived types of
        /// <paramref name="baseType"/>: the <see cref="JsonPolymorphicAttribute.TypeDiscriminatorPropertyName"/> of
        /// its attribute, or <c>$type</c> when the attribute or the name is absent.
        /// </summary>
        /// <param name="baseType">The type whose attributes are read.</param>
        /// <returns>
        /// The property name, or <see langword="null"/> when <paramref name="baseType"/> declares no derived type
        /// with a string discriminator.
        /// </returns>
        public static string? DiscriminatorName(Type baseType)
        {
            if (!DerivedTypeAttributes(baseType).Any(attribute => attribute.TypeDiscriminator is string))
            {
                return null;
            }

            var polymorphic = baseType
                .GetCustomAttributes(typeof(JsonPolymorphicAttribute), false)
                .Cast<JsonPolymorphicAttribute>()
                .FirstOrDefault();
            return polymorphic?.TypeDiscriminatorPropertyName ?? DefaultDiscriminatorName;
        }

        /// <summary>
        /// Returns the string discriminator of <paramref name="subType"/> from the
        /// <see cref="JsonDerivedTypeAttribute"/> that declares it on <paramref name="subType"/> itself or on its
        /// nearest base type.
        /// </summary>
        /// <param name="subType">The derived type whose discriminator is read.</param>
        /// <returns>
        /// The discriminator, or <see langword="null"/> when no attribute declares <paramref name="subType"/> or the
        /// declared discriminator is not a string.
        /// </returns>
        public static string? DiscriminatorValue(Type subType)
        {
            for (var declaringType = subType; declaringType is not null; declaringType = declaringType.BaseType)
            {
                var declaration = DerivedTypeAttributes(declaringType)
                    .FirstOrDefault(attribute => attribute.DerivedType == subType);
                if (declaration is not null)
                {
                    return declaration.TypeDiscriminator as string;
                }
            }

            return null;
        }

        /// <summary>
        /// Removes the discriminator property from the required properties of <paramref name="schema"/> when the
        /// schema declares a discriminator.
        /// </summary>
        /// <param name="schema">The generated schema.</param>
        /// <param name="context">The context of the type the schema was generated for. Not used.</param>
        public void Apply(OpenApiSchema schema, SchemaFilterContext context)
        {
            if (schema.Discriminator?.PropertyName is { } propertyName)
            {
                schema.Required?.Remove(propertyName);
            }
        }

        /// <summary>
        /// Returns the <see cref="JsonDerivedTypeAttribute"/> attributes declared on <paramref name="type"/>.
        /// </summary>
        /// <param name="type">The type whose own attributes are read.</param>
        /// <returns>The declared attributes.</returns>
        private static IEnumerable<JsonDerivedTypeAttribute> DerivedTypeAttributes(Type type) =>
            type.GetCustomAttributes(typeof(JsonDerivedTypeAttribute), false).Cast<JsonDerivedTypeAttribute>();
    }

    /// <summary>The database health check reported when <c>ConnectionStrings:Erewhon</c> is absent or empty.</summary>
    private sealed class MissingConnectionStringHealthCheck : IHealthCheck
    {
        /// <summary>Reports the registration's failure status with a description naming the missing setting.</summary>
        /// <param name="context">The context of the health-check run.</param>
        /// <param name="cancellationToken">Not used.</param>
        /// <returns>The failure result.</returns>
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HealthCheckResult(
                context.Registration.FailureStatus,
                $"ConnectionStrings:{ConnectionStringName} is not set."));
    }

    /// <summary>
    /// The database health check that creates and runs an inner check on every run and reports every result other
    /// than <c>Healthy</c>, and every exception that creating or running the inner check throws, with a description
    /// naming only <c>ConnectionStrings:Erewhon</c>, the exception types and the SQLSTATE, and with no exception and
    /// no data.
    /// </summary>
    /// <param name="createInner">Creates the check that opens the connection and runs <c>SELECT 1</c>.</param>
    private sealed class SanitizedDatabaseHealthCheck(Func<IHealthCheck> createInner) : IHealthCheck
    {
        /// <summary>The text between two exception type names in a description.</summary>
        private const string TypeSeparator = " ---> ";

        /// <summary>Creates the check that is run.</summary>
        private readonly Func<IHealthCheck> _createInner =
            createInner ?? throw new ArgumentNullException(nameof(createInner));

        /// <summary>
        /// Creates the inner check, runs it with <paramref name="context"/> and <paramref name="cancellationToken"/>
        /// and returns a <c>Healthy</c> result unchanged. Any other result is replaced by a result of the same status,
        /// and an exception that creating or running the inner check throws by a result of the registration's failure
        /// status, each with the description of <see cref="Describe"/>, no exception and no data. An
        /// <see cref="OperationCanceledException"/> thrown while <paramref name="cancellationToken"/> is cancelled
        /// propagates.
        /// </summary>
        /// <param name="context">The context of the health-check run.</param>
        /// <param name="cancellationToken">Cancels the run; passed to the inner check.</param>
        /// <returns>The inner check's <c>Healthy</c> result, or its replacement.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="cancellationToken"/> is cancelled and the inner check threw for it.
        /// </exception>
        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            HealthCheckResult result;
            try
            {
                result = await _createInner().CheckHealthAsync(context, cancellationToken);
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return new HealthCheckResult(context.Registration.FailureStatus, Describe(exception));
            }

            return result.Status == HealthStatus.Healthy
                ? result
                : new HealthCheckResult(result.Status, Describe(result.Exception));
        }

        /// <summary>
        /// Returns the description of a failed check: that opening <c>ConnectionStrings:Erewhon</c> or running
        /// <c>SELECT 1</c> failed, followed, when <paramref name="exception"/> is not <see langword="null"/>, by the
        /// full type names of it and its inner exceptions, outermost first, and by the SQLSTATE of the first of them
        /// that is a <see cref="System.Data.Common.DbException"/> with one.
        /// </summary>
        /// <param name="exception">The exception of the failure, or <see langword="null"/> when there is none.</param>
        /// <returns>The description.</returns>
        private static string Describe(Exception? exception)
        {
            var failure = $"Opening ConnectionStrings:{ConnectionStringName} or running SELECT 1 failed";
            if (exception is null)
            {
                return failure + ".";
            }

            var typeNames = new List<string>();
            string? sqlState = null;
            for (var current = exception; current is not null; current = current.InnerException)
            {
                var type = current.GetType();
                typeNames.Add(type.FullName ?? type.Name);
                if (sqlState is null && current is System.Data.Common.DbException { SqlState: { Length: > 0 } state })
                {
                    sqlState = state;
                }
            }

            var typeChain = string.Join(TypeSeparator, typeNames);
            return sqlState is null
                ? $"{failure} with {typeChain}."
                : $"{failure} with {typeChain}, SQLSTATE {sqlState}.";
        }
    }
}
