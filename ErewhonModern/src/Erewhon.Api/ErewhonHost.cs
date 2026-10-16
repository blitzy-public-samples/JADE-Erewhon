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
    private const int ShutdownExitCode = 0;

    /// <summary>
    /// Creates the web application builder from <paramref name="args"/>, passes it to <paramref name="configure"/>
    /// before any service is registered, registers every Erewhon service, and builds the application with its whole
    /// request pipeline and every endpoint mapped. The returned application has not been started.
    /// <list type="bullet">
    /// <item><description>
    /// Services: Serilog from the <c>Serilog</c> configuration section plus every Serilog sink, enricher and filter
    /// registered in dependency injection, leaving the static <see cref="Log.Logger"/> unchanged;
    /// <c>AddErewhonApplication</c> and <c>AddErewhonPersistence</c>; the HTTP context accessor; a singleton
    /// <see cref="SessionRegistry"/>; a scoped <see cref="HttpSessionContext"/> as <see cref="ISessionContext"/>; a
    /// singleton <see cref="SignalRNotificationPublisher"/> as <see cref="INotificationPublisher"/>; a singleton
    /// <see cref="ClosureScheduleRegistry"/> and the hosted <see cref="TenderClosureScheduleService"/>; transient
    /// <see cref="ErewhonWebService"/> and <see cref="ErewhonWebServiceAdmin"/>; the controllers of this assembly with
    /// the global <see cref="RestExceptionFilter"/> and no implicit required attribute on non-nullable reference
    /// types; CoreWCF with metadata addresses taken from the request headers; SignalR; Swashbuckle with the
    /// documents <c>rest</c> and <c>app</c>; the PostgreSQL health check <c>postgresql</c>, which reads
    /// <c>ConnectionStrings:Erewhon</c> each time it runs and reports <c>Unhealthy</c> when the value is absent or
    /// empty, or when a connection cannot be opened or <c>SELECT 1</c> fails.
    /// </description></item>
    /// <item><description>
    /// JSON for controllers, minimal APIs and SignalR: camelCase property names, and enums written as camelCase
    /// strings.
    /// </description></item>
    /// <item><description>
    /// Pipeline, in order: Serilog request logging through the host's logger; <see cref="ErrorOutcomeMiddleware"/>;
    /// static files; the OpenAPI
    /// documents at <c>/swagger/{document}/swagger.json</c> and the UI at <c>/swagger</c>;
    /// <see cref="SessionMiddleware"/>; the two SOAP endpoints on SOAP 1.1 <c>BasicHttpBinding</c> with WSDL at
    /// <c>?wsdl</c>; the REST controllers; the app endpoint groups under <c>/api/app</c>; the hub at
    /// <c>/hubs/erewhon</c>; the health check at <c>/healthz</c>, answering <c>Healthy</c> with 200 or
    /// <c>Unhealthy</c> with 503; and <c>index.html</c> for every other path without a file extension.
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

        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        AddServices(builder);

        var app = builder.Build();
        MapPipeline(app);
        return app;
    }

    /// <summary>
    /// Runs the process: when <see cref="CommandLineVerbs.IsVerb"/> recognises <paramref name="args"/>, builds the
    /// host without command-line configuration, runs the verb through <see cref="CommandLineVerbs.RunAsync"/> without
    /// starting the web server or the hosted services, and disposes the host; otherwise builds the host from
    /// <paramref name="args"/>, runs the web server until it shuts down, and disposes the host.
    /// </summary>
    /// <param name="args">The process arguments.</param>
    /// <returns>The verb's exit code, or 0 once the web server has shut down.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (CommandLineVerbs.IsVerb(args))
        {
            await using var verbHost = Build(Array.Empty<string>());
            return await CommandLineVerbs.RunAsync(verbHost.Services, args, Console.Out);
        }

        var app = Build(args);
        await app.RunAsync();
        return ShutdownExitCode;
    }

    /// <summary>Registers the logging, layer, session, realtime, closure, SOAP, REST, OpenAPI and health services.</summary>
    /// <param name="builder">The builder whose services are registered.</param>
    private static void AddServices(WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog(
            (context, services, configuration) => configuration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext(),
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
    /// A <see cref="MissingConnectionStringHealthCheck"/> when the value is absent or empty; otherwise an
    /// <see cref="NpgSqlHealthCheck"/> that opens a connection with the value and runs <c>SELECT 1</c>.
    /// </returns>
    private static IHealthCheck CreateDatabaseHealthCheck(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        return string.IsNullOrEmpty(connectionString)
            ? new MissingConnectionStringHealthCheck()
            : new NpgSqlHealthCheck(new NpgSqlHealthCheckOptions(connectionString));
    }

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
        app.MapFallbackToFile(SpaFallbackFile);
    }

    /// <summary>Sets camelCase property names and adds the camelCase string enum converter.</summary>
    /// <param name="options">The serializer options to configure.</param>
    private static void ConfigureJson(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
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
}
