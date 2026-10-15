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
/// <param name="Kind">The outcome kind: <see cref="KindException"/>, <see cref="KindLock"/> or <see cref="KindDeadlock"/>.</param>
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
/// Turns an exception that escapes the rest of the request pipeline into status 500 with an <see cref="ErrorOutcome"/>
/// JSON body, and writes one error log event for every outcome except the lock outcome.
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
        "Unhandled exception {ErrorCode}: {ErrorText} (error item {ErrorItem}) in session {SessionId}, role {Role}, agent {AgentId}, client {ClientId}";

    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    private readonly ILogger<ErrorOutcomeMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Runs the rest of the pipeline. When it throws before the response has started, clears the response and writes
    /// status 500 with the <see cref="ErrorOutcome"/> of the exception as <c>application/json</c>, after logging it
    /// when <see cref="IsLogged"/> is <see langword="true"/>. An exception thrown after the response has started
    /// propagates unchanged.
    /// </summary>
    /// <param name="context">The HTTP context of the request.</param>
    /// <returns>A task that completes when the response has been produced.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await _next(context);
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            var services = context.RequestServices;
            var translated = Translate(exception, services?.GetService<PostgresErrorMapper>());
            var outcome = Create(translated);

            if (IsLogged(translated))
            {
                LogOutcome(_logger, translated, services?.GetService<ISessionContext>());
            }

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
    /// Writes one error event with <see cref="UnhandledExceptionEventId"/> and <paramref name="translated"/> attached,
    /// carrying the properties <c>ErrorCode</c>, <c>ErrorText</c> and <c>ErrorItem</c>, plus <c>SessionId</c>,
    /// <c>Role</c>, <c>AgentId</c> and <c>ClientId</c> when <paramref name="session"/> has a session.
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

        var (code, text, item) = Describe(translated);
        if (session is { HasSession: true })
        {
            logger.LogError(
                UnhandledExceptionEventId,
                translated,
                SessionLogTemplate,
                code,
                text,
                item,
                session.SessionId,
                session.Role,
                session.AgentId,
                session.ClientId);
        }
        else
        {
            logger.LogError(UnhandledExceptionEventId, translated, LogTemplate, code, text, item);
        }
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

    private static string HResultText(Exception exception) =>
        exception.HResult.ToString(CultureInfo.InvariantCulture);
}
