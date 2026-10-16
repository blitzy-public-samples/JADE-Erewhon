using System.Net;
using System.Text;
using System.Text.Json;
using Erewhon.Api.Errors;
using Erewhon.Api.Sessions;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Services;
using Erewhon.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;

namespace Erewhon.Api.Tests.CrossCutting;

/// <summary>
/// Characterises the outcome of a request the framework cannot bind or read. The HTTP cases build and start the real
/// API on Kestrel over the assembly database in the Production environment, in the Development environment, and in
/// the Production environment with the minimal-API route handlers set to throw on a bad request, send one app request
/// with a body that cannot be bound, and read the events the host's Serilog logger received. The middleware cases run
/// <see cref="ErrorOutcomeMiddleware"/> alone over a request delegate that throws.
/// </summary>
public sealed class BadRequestOutcomeTests
{
    private const string ConnectionStringKey = "ConnectionStrings:Erewhon";

    private const string JsonMediaType = "application/json";

    private const string SessionsPath = "api/app/sessions";

    private const string AgentsPath = "api/app/admin/agents";

    private const string ShopSearchPath = "api/app/shop/search";

    private const string CartItemsPath = "api/app/shop/cart/items";

    private const string RequestPathProperty = "RequestPath";

    private const string StatusCodeProperty = "StatusCode";

    private const string SourceContextProperty = "SourceContext";

    private const string RequestLoggingSourceContext = "Serilog.AspNetCore.RequestLoggingMiddleware";

    private const string EventIdProperty = "EventId";

    private const string PartialBody = "{\"partial\":";

    /// <summary>How the host under test is built.</summary>
    public enum HostMode
    {
        /// <summary>The Production environment with the host's own options.</summary>
        Production,

        /// <summary>The Development environment with the host's own options.</summary>
        Development,

        /// <summary>The Production environment with <see cref="RouteHandlerOptions.ThrowOnBadRequest"/> set afterwards.</summary>
        ThrowingRouteHandlers,
    }

    /// <summary>The session the request carries.</summary>
    public enum RequestSession
    {
        /// <summary>No session header.</summary>
        None,

        /// <summary>A stored administrator session of the admin area.</summary>
        Administrator,

        /// <summary>A stored client session of the shop area.</summary>
        Client,
    }

    /// <summary>
    /// Every host mode with each <c>application/json</c> app request whose body cannot be bound: the path, the
    /// session and the body.
    /// </summary>
    public static TheoryData<HostMode, string, RequestSession, string> UnboundRequests
    {
        get
        {
            var requests = new (string Path, RequestSession Session, string Body)[]
            {
                (SessionsPath, RequestSession.None, "{\"area\":"),
                (AgentsPath, RequestSession.Administrator, "{\"name\": \"QA\", \"address\": "),
                (ShopSearchPath, RequestSession.Client, "{\"priceLow\":\"abc\"}"),
                (CartItemsPath, RequestSession.Client, "{\"itemId\":\"8f9d6c3e-55a1-4a43-9a0e-3c7d2b1e6f40\",\"offer\":0}"),
            };

            var data = new TheoryData<HostMode, string, RequestSession, string>();
            foreach (HostMode mode in Enum.GetValues<HostMode>())
            {
                foreach ((string path, RequestSession session, string body) in requests)
                {
                    data.Add(mode, path, session, body);
                }
            }

            return data;
        }
    }

    /// <summary>The statuses of the <see cref="BadHttpRequestException"/> cases.</summary>
    public static TheoryData<int> BadRequestStatusCodes => new()
    {
        StatusCodes.Status400BadRequest,
        StatusCodes.Status413PayloadTooLarge,
        StatusCodes.Status415UnsupportedMediaType,
    };

    /// <summary>
    /// An app request whose JSON body is malformed or wrongly typed is answered with status 400 and an empty body,
    /// with no <see cref="ErrorOutcomeMiddleware.UnhandledExceptionEventId"/> event and no error-level event, and its
    /// HTTP request event is at <see cref="LogEventLevel.Information"/> with status 400, in every host mode. The route
    /// handlers throw on a bad request only when that is set after the host's own registrations.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnboundRequests))]
    public async Task UnboundAppRequestIsAnsweredWithoutErrorOutcome(
        HostMode mode,
        string path,
        RequestSession session,
        string body)
    {
        var sink = new CollectingLogSink();
        WebApplication app = await StartHostAsync(mode, sink);
        try
        {
            Assert.Equal(mode == HostMode.Development, app.Environment.IsDevelopment());
            Assert.Equal(
                mode == HostMode.ThrowingRouteHandlers,
                app.Services.GetRequiredService<IOptions<RouteHandlerOptions>>().Value.ThrowOnBadRequest);

            using var client = new HttpClient { BaseAddress = BaseAddress(app) };
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(body, Encoding.UTF8, JsonMediaType),
            };
            if (CreateSession(app.Services.GetRequiredService<SessionRegistry>(), session) is { } sessionId)
            {
                request.Headers.Add(SessionMiddleware.HeaderName, sessionId);
            }

            using HttpResponseMessage response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
            Assert.Null(response.Content.Headers.ContentType);

            Assert.DoesNotContain(sink.Events, IsUnhandledExceptionEvent);
            Assert.Empty(sink.ErrorEvents);
            LogEvent summary = Assert.Single(
                sink.Events,
                logEvent => HasProperty(logEvent, SourceContextProperty, RequestLoggingSourceContext)
                    && HasProperty(logEvent, RequestPathProperty, "/" + path));
            Assert.Equal(LogEventLevel.Information, summary.Level);
            Assert.Equal(new ScalarValue(StatusCodes.Status400BadRequest), summary.Properties[StatusCodeProperty]);
        }
        finally
        {
            await StopAsync(app);
        }
    }

    /// <summary>
    /// A <see cref="BadHttpRequestException"/> that escapes before the response has started clears the response,
    /// which had a content type and part of a body, and answers the exception's own status with no body and no log
    /// event.
    /// </summary>
    [Theory]
    [MemberData(nameof(BadRequestStatusCodes))]
    public async Task EscapedBadRequestIsAnsweredWithItsStatusAndNoEvent(int statusCode)
    {
        var logger = new RecordingLogger();
        var middleware = new ErrorOutcomeMiddleware(
            async context =>
            {
                context.Response.ContentType = JsonMediaType;
                await context.Response.WriteAsync(PartialBody);
                throw new BadHttpRequestException("The request was rejected.", statusCode);
            },
            logger);
        using var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;

        await middleware.InvokeAsync(context);

        Assert.Equal(statusCode, context.Response.StatusCode);
        Assert.Null(context.Response.ContentType);
        Assert.Equal(0, body.Length);
        Assert.Empty(logger.Entries);
    }

    /// <summary>
    /// A <see cref="BadHttpRequestException"/> that escapes after the response has started aborts the request, leaves
    /// the started status unchanged and writes no log event.
    /// </summary>
    [Fact]
    public async Task BadRequestAfterResponseStartedAbortsWithoutEvent()
    {
        var logger = new RecordingLogger();
        var lifetime = new RecordingLifetimeFeature();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        var middleware = new ErrorOutcomeMiddleware(
            _ => throw new BadHttpRequestException("The request was rejected."),
            logger);

        await middleware.InvokeAsync(context);

        Assert.True(lifetime.Aborted);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Empty(logger.Entries);
    }

    /// <summary>
    /// Any other exception that escapes before the response has started still gets status 500 with the exception
    /// <see cref="ErrorOutcome"/> and one error event with <see cref="ErrorOutcomeMiddleware.UnhandledExceptionEventId"/>.
    /// </summary>
    [Fact]
    public async Task OtherExceptionStillGetsErrorOutcomeAndEvent()
    {
        var logger = new RecordingLogger();
        var middleware = new ErrorOutcomeMiddleware(
            _ => throw new InvalidOperationException("The server failed."),
            logger);
        using var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        using JsonDocument outcome = JsonDocument.Parse(body.ToArray());
        Assert.Equal(ErrorOutcome.KindException, outcome.RootElement.GetProperty("kind").GetString());
        (LogLevel level, EventId eventId) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, level);
        Assert.Equal(ErrorOutcomeMiddleware.UnhandledExceptionEventId, eventId);
    }

    /// <summary>
    /// Builds the real API through <see cref="ErewhonHost.Build"/> in the environment of <paramref name="mode"/>,
    /// listening on <see cref="ErewhonApiHost.DefaultUrl"/>, over the assembly database, with <paramref name="sink"/>
    /// as a Serilog sink and, for <see cref="HostMode.ThrowingRouteHandlers"/>, the route handlers set to throw on a
    /// bad request after every other configuration; then starts it.
    /// </summary>
    private static async Task<WebApplication> StartHostAsync(HostMode mode, CollectingLogSink sink)
    {
        PostgresDatabase database = PostgresDatabase.ForAssembly(typeof(BadRequestOutcomeTests).Assembly);
        await database.EnsureCreatedAndMigratedAsync();

        string environment = mode == HostMode.Development ? Environments.Development : Environments.Production;
        WebApplication app = ErewhonHost.Build(
            ["--environment", environment, "--urls", ErewhonApiHost.DefaultUrl],
            builder =>
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [ConnectionStringKey] = database.ConnectionString,
                });
                builder.Services.AddSingleton<ILogEventSink>(sink);
                if (mode == HostMode.ThrowingRouteHandlers)
                {
                    builder.Services.PostConfigure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
                }
            });

        try
        {
            await app.StartAsync();
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    /// <summary>Stops <paramref name="app"/> and then disposes it, also when stopping fails.</summary>
    private static async Task StopAsync(WebApplication app)
    {
        try
        {
            await app.StopAsync();
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>Returns the first <c>http</c> address the started application listens on, ending in <c>/</c>.</summary>
    private static Uri BaseAddress(WebApplication app)
    {
        string address = app.Urls.First(url => url.StartsWith(Uri.UriSchemeHttp + Uri.SchemeDelimiter, StringComparison.OrdinalIgnoreCase));
        return new Uri(address.EndsWith('/') ? address : address + "/", UriKind.Absolute);
    }

    /// <summary>Stores a session of the kind <paramref name="session"/> names and returns its id, or <see langword="null"/> for none.</summary>
    private static string? CreateSession(SessionRegistry sessions, RequestSession session) =>
        session switch
        {
            RequestSession.None => null,
            RequestSession.Administrator => sessions
                .Create(LogonArea.Admin, SessionRole.Administrator, agentId: null, clientId: null, "Bad Request Company")
                .SessionId,
            RequestSession.Client => sessions
                .Create(LogonArea.Shop, SessionRole.Client, agentId: null, clientId: 1, "Bad Request Client")
                .SessionId,
            _ => throw new ArgumentOutOfRangeException(nameof(session), session, null),
        };

    /// <summary>Returns whether the event has the property <paramref name="name"/> with the string <paramref name="value"/>.</summary>
    private static bool HasProperty(LogEvent logEvent, string name, string value) =>
        logEvent.Properties.TryGetValue(name, out LogEventPropertyValue? property)
        && Equals(property, new ScalarValue(value));

    /// <summary>Returns whether the event's <c>EventId</c> is <see cref="ErrorOutcomeMiddleware.UnhandledExceptionEventId"/>.</summary>
    private static bool IsUnhandledExceptionEvent(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue(EventIdProperty, out LogEventPropertyValue? eventId)
        && eventId is StructureValue structure
        && structure.Properties.Any(property =>
            property.Name == "Id" && Equals(property.Value, new ScalarValue(ErrorOutcomeMiddleware.UnhandledExceptionEventId.Id)));

    /// <summary>A logger that keeps the level and event id of every entry it receives, in order.</summary>
    private sealed class RecordingLogger : ILogger<ErrorOutcomeMiddleware>
    {
        public List<(LogLevel Level, EventId EventId)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId));
    }

    /// <summary>A response feature whose response has already started.</summary>
    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    /// <summary>A request lifetime feature that records whether the request was aborted.</summary>
    private sealed class RecordingLifetimeFeature : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }

        public bool Aborted { get; private set; }

        public void Abort() => Aborted = true;
    }
}
