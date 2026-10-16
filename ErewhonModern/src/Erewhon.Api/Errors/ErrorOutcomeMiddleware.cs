using System.Globalization;
using System.Text.Json.Serialization;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Messages;
using Erewhon.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Erewhon.Api.Errors;

/// <summary>
/// The JSON body of an error response: the outcome kind and the heading and body of the message shown to the user.
/// </summary>
/// <param name="Kind">The outcome kind: <c>exception</c>, <c>lock</c> or <c>deadlock</c>.</param>
/// <param name="Heading">The message heading.</param>
/// <param name="Body">The message body.</param>
public sealed record ErrorOutcome(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("heading")] string Heading,
    [property: JsonPropertyName("body")] string Body)
{
    /// <summary>The kind of an unexpected error, including the 500002 and 500006 application errors.</summary>
    public const string KindException = "exception";

    /// <summary>The kind of a lock that could not be obtained.</summary>
    public const string KindLock = "lock";

    /// <summary>The kind of a detected deadlock.</summary>
    public const string KindDeadlock = "deadlock";
}

/// <summary>
/// Turns an exception that escapes the rest of the request pipeline before the response has started into status 500
/// with an <see cref="ErrorOutcome"/> JSON body, and aborts the request when one escapes after the response has
/// started. In both cases it writes one error log event, as <see cref="LogOutcome"/> describes, for every outcome
/// except the lock outcome, and does not rethrow the exception. An escaped <see cref="BadHttpRequestException"/>
/// writes no log event: before the response has started it is answered with its own
/// <see cref="BadHttpRequestException.StatusCode"/> and an empty body, and after it the request is aborted.
/// </summary>
/// <param name="next">The next middleware in the pipeline.</param>
/// <param name="logger">The logger that receives the error events.</param>
public sealed class ErrorOutcomeMiddleware(RequestDelegate next, ILogger<ErrorOutcomeMiddleware> logger)
{
    /// <summary>The event id of the error event written for an unhandled exception.</summary>
    public static readonly EventId UnhandledExceptionEventId = new(500, "ErewhonUnhandledException");

    private const string LogTemplate =
        "Unhandled exception {ErrorCode}: {ErrorText} (error item {ErrorItem})";

    private const string SessionLogTemplate =
        "Unhandled exception {ErrorCode}: {ErrorText} (error item {ErrorItem}) for role {Role}, agent {AgentId}, client {ClientId}";

    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    private readonly ILogger<ErrorOutcomeMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Runs the rest of the pipeline. When it throws a <see cref="BadHttpRequestException"/>, writes no log event;
    /// before the response has started, it clears the response and sets the exception's
    /// <see cref="BadHttpRequestException.StatusCode"/> with no body; after the response has started, it aborts the
    /// request with <see cref="HttpContext.Abort"/>; in both cases it returns without rethrowing. When it throws any
    /// other exception, translates the exception with <see cref="Translate"/> and, when <see cref="IsLogged"/> is
    /// <see langword="true"/>, writes one event through <see cref="LogOutcome"/>. Before the response has started, it
    /// then clears the response and writes status 500 with the <see cref="ErrorOutcome"/> of the translated exception
    /// as <c>application/json</c>; after the response has started, it writes nothing more to the response, aborts the
    /// request with <see cref="HttpContext.Abort"/> and returns without rethrowing.
    /// </summary>
    /// <param name="context">The HTTP context of the request.</param>
    /// <returns>A task that completes when the response has been produced or the request aborted.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await _next(context);
        }
        catch (BadHttpRequestException badRequest)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
                return;
            }

            context.Response.Clear();
            context.Response.StatusCode = badRequest.StatusCode;
        }
        catch (Exception exception)
        {
            var services = context.RequestServices;
            var translated = Translate(exception, services?.GetService<PostgresErrorMapper>());

            if (IsLogged(translated))
            {
                LogOutcome(_logger, translated, services?.GetService<ISessionContext>());
            }

            if (context.Response.HasStarted)
            {
                context.Abort();
                return;
            }

            var outcome = Create(translated);
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(outcome, context.RequestAborted);
        }
    }

    /// <summary>
    /// Returns the exception whose outcome is reported for <paramref name="exception"/>.
    /// </summary>
    /// <param name="exception">The exception that escaped the pipeline.</param>
    /// <param name="mapper">The database error mapper, or <see langword="null"/> when none is registered.</param>
    /// <returns>
    /// The first exception that is an <see cref="ErewhonLockException"/>, <see cref="ErewhonDeadlockException"/>,
    /// <see cref="DatabaseFailureException"/> or <see cref="IJadeException"/>, walking <paramref name="exception"/>,
    /// its inner-exception chain and the inner exceptions of any <see cref="AggregateException"/> depth first;
    /// otherwise the exception that <paramref name="mapper"/> maps <paramref name="exception"/> to; otherwise
    /// <paramref name="exception"/> unchanged.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    public static Exception Translate(Exception exception, PostgresErrorMapper? mapper)
    {
        ArgumentNullException.ThrowIfNull(exception);

        foreach (var step in Chain(exception))
        {
            if (IsTranslated(step))
            {
                return step;
            }
        }

        return mapper?.Map(exception) ?? exception;
    }

    /// <summary>
    /// Returns the error outcome of an exception already passed through <see cref="Translate"/>.
    /// </summary>
    /// <param name="exception">The translated exception.</param>
    /// <returns>
    /// The lock outcome with the web lock heading and body for an <see cref="ErewhonLockException"/>; the deadlock
    /// outcome with the deadlock heading and web body for an <see cref="ErewhonDeadlockException"/>; an exception
    /// outcome titled "Error" with the minimum-above-maximum price message for
    /// <see cref="ErewhonException.MinHigherThanMax"/> and with the user-not-selected message for
    /// <see cref="ErewhonException.UserNotProvided"/>; otherwise an exception outcome with the exception heading
    /// and web body for the exception's error code and text.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    public static ErrorOutcome Create(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        switch (exception)
        {
            case ErewhonLockException:
                return new ErrorOutcome(
                    ErrorOutcome.KindLock,
                    MessageCatalog.Get(MessageKeys.BaseLockMessageHeading),
                    MessageCatalog.Get(
                        MessageKeys.BaseLockMessageBodyWeb,
                        "",
                        MessageCatalog.Get(MessageKeys.BaseLockMessageUnknownUser),
                        "0"));

            case ErewhonDeadlockException:
                return new ErrorOutcome(
                    ErrorOutcome.KindDeadlock,
                    MessageCatalog.Get(MessageKeys.BaseDeadlockHeading),
                    MessageCatalog.Get(MessageKeys.BaseDeadlockBodyWeb, ""));

            case ErewhonException { ErrorCode: ErewhonException.MinHigherThanMax }:
                return new ErrorOutcome(
                    ErrorOutcome.KindException,
                    MessageCatalog.Get(MessageKeys.Error),
                    MessageCatalog.Get(MessageKeys.MinPriceGreaterThanMaxError));

            case ErewhonException { ErrorCode: ErewhonException.UserNotProvided }:
                return new ErrorOutcome(
                    ErrorOutcome.KindException,
                    MessageCatalog.Get(MessageKeys.Error),
                    MessageCatalog.Get(MessageKeys.UserNotSelected));

            default:
                var (code, text, item) = Describe(exception);
                return new ErrorOutcome(
                    ErrorOutcome.KindException,
                    MessageCatalog.Get(MessageKeys.BaseExceptionHeading, code),
                    MessageCatalog.Get(MessageKeys.BaseExceptionBodyWeb, code, text, item, ""));
        }
    }

    /// <summary>
    /// Returns whether an error event is written for a translated exception.
    /// </summary>
    /// <param name="translated">The exception returned by <see cref="Translate"/>.</param>
    /// <returns><see langword="false"/> for an <see cref="ErewhonLockException"/>; otherwise <see langword="true"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="translated"/> is <see langword="null"/>.</exception>
    public static bool IsLogged(Exception translated)
    {
        ArgumentNullException.ThrowIfNull(translated);
        return translated is not ErewhonLockException;
    }

    /// <summary>
    /// Writes one error event with <see cref="UnhandledExceptionEventId"/>, carrying the properties
    /// <c>ErrorCode</c>, <c>ErrorText</c> and <c>ErrorItem</c>, plus <c>Role</c>, <c>AgentId</c> and
    /// <c>ClientId</c> when <paramref name="session"/> has a session. <c>ErrorCode</c> and <c>ErrorItem</c> are the
    /// error code and error item of the exception outcome; <c>ErrorText</c> is the message of an
    /// <see cref="IJadeException"/> or a <see cref="MissingConnectionStringException"/> and the full name of the
    /// exception type for any other exception. The attached exception has <c>ErrorText</c> as its message and no
    /// inner exception; its string form lists <paramref name="translated"/>, its inner exceptions and the inner
    /// exceptions of any <see cref="AggregateException"/>, depth first, each as its type's full name, followed for an
    /// <see cref="IJadeException"/> by its error code and message and for a
    /// <see cref="MissingConnectionStringException"/> by its message, and by its stack trace when it has one; its
    /// stack trace is the first stack trace in that list.
    /// </summary>
    /// <param name="logger">The logger that receives the event.</param>
    /// <param name="translated">The exception returned by <see cref="Translate"/>.</param>
    /// <param name="session">The session context of the request, or <see langword="null"/> when none is available.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="logger"/> or <paramref name="translated"/> is <see langword="null"/>.
    /// </exception>
    public static void LogOutcome(ILogger logger, Exception translated, ISessionContext? session)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(translated);

        var (code, text, item) = DescribeForLog(translated);
        var diagnostic = new SanitizedException(translated, text);
        if (session is { HasSession: true })
        {
            logger.LogError(
                UnhandledExceptionEventId,
                diagnostic,
                SessionLogTemplate,
                code,
                text,
                item,
                session.Role,
                session.AgentId,
                session.ClientId);
        }
        else
        {
            logger.LogError(UnhandledExceptionEventId, diagnostic, LogTemplate, code, text, item);
        }
    }

    /// <summary>
    /// Returns the rendering of <paramref name="exception"/> that <see cref="LogOutcome"/> attaches to its event.
    /// </summary>
    /// <param name="exception">The exception to render.</param>
    /// <returns>
    /// An exception whose message is the message of an <see cref="IJadeException"/> or a
    /// <see cref="MissingConnectionStringException"/> and the full name of the exception type for any other
    /// exception, and which has no inner exception. Its string form lists <paramref name="exception"/>, its inner
    /// exceptions and the inner exceptions of any <see cref="AggregateException"/>, depth first, each as its type's
    /// full name, followed for an <see cref="IJadeException"/> by its error code and message and for a
    /// <see cref="MissingConnectionStringException"/> by its message, and by its stack trace when it has one; its
    /// stack trace is the first stack trace in that list.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    public static Exception Sanitize(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new SanitizedException(exception, DescribeForLog(exception).Text);
    }

    private static bool IsTranslated(Exception exception) =>
        exception is ErewhonLockException or ErewhonDeadlockException or DatabaseFailureException or IJadeException;

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;

            if (current is AggregateException aggregate)
            {
                for (var i = aggregate.InnerExceptions.Count - 1; i >= 0; i--)
                {
                    pending.Push(aggregate.InnerExceptions[i]);
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
    }

    private static (string Code, string Text, string Item) Describe(Exception exception)
    {
        var (code, text) = exception switch
        {
            DatabaseFailureException database
                when !string.IsNullOrEmpty(database.SqlState) && !string.IsNullOrEmpty(database.DatabaseMessage) =>
                (database.SqlState, database.DatabaseMessage),
            DatabaseFailureException => (HResultText(exception), exception.Message),
            IJadeException jade => (jade.ErrorCode.ToString(CultureInfo.InvariantCulture), exception.Message),
            ErewhonLockException locked => (locked.SqlState, locked.InnerException?.Message ?? locked.Message),
            ErewhonDeadlockException deadlock => (deadlock.SqlState, deadlock.InnerException?.Message ?? deadlock.Message),
            _ => (HResultText(exception), exception.Message),
        };

        var item = exception is StringTooLongException { FieldName: { } fieldName } && fieldName.Trim(' ').Length > 0
            ? fieldName.Trim(' ')
            : MessageCatalog.Get(MessageKeys.BaseNotAvailable);

        return (code, text, item);
    }

    private static (string Code, string Text, string Item) DescribeForLog(Exception exception)
    {
        var (code, _, item) = Describe(exception);
        var text = exception is IJadeException or MissingConnectionStringException
            ? exception.Message
            : TypeName(exception);
        return (code, text, item);
    }

    private static string HResultText(Exception exception) =>
        exception.HResult.ToString(CultureInfo.InvariantCulture);

    private static string TypeName(Exception exception)
    {
        var type = exception.GetType();
        return type.FullName ?? type.Name;
    }

    /// <summary>
    /// The exception attached to a logged event in place of the original: its message is the logged error text, it
    /// has no inner exception, and it renders the exception chain of the exception it stands for as type names, the
    /// error code and message of each <see cref="IJadeException"/>, the message of each
    /// <see cref="MissingConnectionStringException"/>, and stack traces.
    /// </summary>
    private sealed class SanitizedException : Exception
    {
        private const string InnerSeparator = " ---> ";

        private readonly string _rendering;

        private readonly string? _stackTrace;

        public SanitizedException(Exception translated, string text)
            : base(text)
        {
            var segments = new List<string>();
            string? stackTrace = null;
            foreach (var step in Chain(translated))
            {
                var segment = Header(step);
                if (step.StackTrace is { } trace)
                {
                    segment += Environment.NewLine + trace;
                    stackTrace ??= trace;
                }

                segments.Add(segment);
            }

            _rendering = string.Join(Environment.NewLine + InnerSeparator, segments);
            _stackTrace = stackTrace;
        }

        public override string? StackTrace => _stackTrace;

        public override string ToString() => _rendering;

        private static string Header(Exception exception) =>
            exception switch
            {
                IJadeException jade => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{TypeName(exception)} ({jade.ErrorCode}): {exception.Message}"),
                MissingConnectionStringException => $"{TypeName(exception)}: {exception.Message}",
                _ => TypeName(exception),
            };
    }
}
