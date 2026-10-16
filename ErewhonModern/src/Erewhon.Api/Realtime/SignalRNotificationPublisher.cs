using Erewhon.Api.Errors;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Notifications;
using Microsoft.AspNetCore.SignalR;

namespace Erewhon.Api.Realtime;

/// <summary>
/// Sends the notifications of <see cref="INotificationPublisher"/> to the clients connected to <see cref="ErewhonHub"/>.
/// Each member turns its arguments into hub messages, as given, for one hub group, and delivers them in one of two modes:
/// <list type="bullet">
/// <item><description>
/// Deferred, when <see cref="IHttpContextAccessor.HttpContext"/> is set: the call sends nothing and returns once its
/// messages are appended, in call order, to the one buffer of that request. The first call of a request creates the
/// buffer and registers one <see cref="HttpResponse.OnCompleted(Func{object, Task}, object)"/> callback, which, after the
/// response has completed, sends the buffered messages in order, each send awaited before the next, with
/// <see cref="CancellationToken.None"/>, including messages appended while it runs. A send that fails writes one
/// <see cref="LogLevel.Warning"/> event, template <c>Could not send {HubMethod} after the response completed</c>, with
/// the hub method as <c>HubMethod</c> and the rendering of <see cref="ErrorOutcomeMiddleware.Sanitize"/> attached, and
/// the sends after it still run; the group, and so the session id, is not logged. A call made once that callback has
/// sent the whole buffer is delivered immediately. A call whose token is already cancelled throws
/// <see cref="OperationCanceledException"/> and buffers nothing.
/// </description></item>
/// <item><description>
/// Immediate, when no HTTP context is set (the command-line verbs, the closure schedule service and scopes created
/// from the root service provider): the call sends its messages in order, each send awaited before the next, with the
/// caller's token, and returns when the hub has accepted them; a failed send propagates to the caller.
/// </description></item>
/// </list>
/// Arguments are validated when a member is called, in both modes. Safe to call from several threads at once.
/// </summary>
/// <param name="hub">The context of the hub the messages are sent through.</param>
/// <param name="httpContextAccessor">The accessor whose current HTTP context selects the delivery mode.</param>
/// <param name="logger">The logger that receives the failed deferred sends.</param>
public sealed class SignalRNotificationPublisher(
    IHubContext<ErewhonHub> hub,
    IHttpContextAccessor httpContextAccessor,
    ILogger<SignalRNotificationPublisher> logger) : INotificationPublisher
{
    private const string DeferredSendFailedTemplate = "Could not send {HubMethod} after the response completed";

    private static readonly object DeferredSendsKey = new();

    private readonly object _gate = new();

    /// <summary>
    /// Delivers one <see cref="ErewhonHub.EntityChanged"/> message per event to the group
    /// <see cref="ErewhonHub.AllGroup"/>, in list order, deferred to the end of the current request's response or
    /// immediately as the class describes. Each message carries an <see cref="EntityChangedMessage"/> built from
    /// <see cref="ChangeEvent.EntityType"/>, <see cref="ChangeEvent.Id"/> and <see cref="ChangeEvent.Kind"/>;
    /// <see cref="ChangeEvent.Info"/> is not sent. An empty list sends and buffers nothing.
    /// </summary>
    /// <param name="events">The committed changes to announce, in the order they are sent.</param>
    /// <param name="ct">Cancels the immediate sending; during a request, a token cancelled at the call cancels it.</param>
    /// <returns>
    /// A task that completes when every message has been handed to the hub, or, during a request, appended to the
    /// request's buffer.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="events"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public Task PublishEntityChangesAsync(IReadOnlyList<ChangeEvent> events, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        var sends = new HubSend[events.Count];
        for (var index = 0; index < sends.Length; index++)
        {
            ChangeEvent change = events[index];
            sends[index] = new HubSend(
                ErewhonHub.AllGroup,
                ErewhonHub.EntityChanged,
                new EntityChangedMessage(change.EntityType, change.Id, change.Kind));
        }

        return DeliverAsync(sends, ct);
    }

    /// <summary>
    /// Delivers one <see cref="ErewhonHub.CartItemDeleted"/> message carrying a <see cref="CartItemDeletedMessage"/> with
    /// <paramref name="itemId"/> to the group <see cref="ErewhonHub.SessionGroup(string)"/> of
    /// <paramref name="sessionId"/>, deferred to the end of the current request's response or immediately as the class
    /// describes.
    /// </summary>
    /// <param name="sessionId">Id of the session whose cart held the deleted item.</param>
    /// <param name="itemId">Internal row id of the deleted item.</param>
    /// <param name="ct">Cancels the immediate sending; during a request, a token cancelled at the call cancels it.</param>
    /// <returns>
    /// A task that completes when the message has been handed to the hub, or, during a request, appended to the
    /// request's buffer.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public Task PublishCartItemDeletedAsync(string sessionId, long itemId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        return DeliverAsync(
            [new HubSend(ErewhonHub.SessionGroup(sessionId), ErewhonHub.CartItemDeleted, new CartItemDeletedMessage(itemId))],
            ct);
    }

    /// <summary>
    /// Delivers one <see cref="ErewhonHub.SaveOutstandingChanges"/> message carrying a
    /// <see cref="SaveOutstandingChangesMessage"/> with <paramref name="entityType"/> and <paramref name="id"/> to the
    /// group <see cref="ErewhonHub.SessionGroup(string)"/> of <paramref name="sessionId"/>, deferred to the end of the
    /// current request's response or immediately as the class describes.
    /// </summary>
    /// <param name="sessionId">Id of the session editing the row.</param>
    /// <param name="entityType">Domain type name of the row being edited, such as <c>Agent</c>.</param>
    /// <param name="id">Internal row id of the row being edited.</param>
    /// <param name="ct">Cancels the immediate sending; during a request, a token cancelled at the call cancels it.</param>
    /// <returns>
    /// A task that completes when the message has been handed to the hub, or, during a request, appended to the
    /// request's buffer.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> or <paramref name="entityType"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> or <paramref name="entityType"/> is empty.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public Task PublishSaveOutstandingChangesAsync(
        string sessionId,
        string entityType,
        long id,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentException.ThrowIfNullOrEmpty(entityType);

        return DeliverAsync(
            [
                new HubSend(
                    ErewhonHub.SessionGroup(sessionId),
                    ErewhonHub.SaveOutstandingChanges,
                    new SaveOutstandingChangesMessage(entityType, id)),
            ],
            ct);
    }

    /// <summary>
    /// Appends <paramref name="sends"/> to the buffer of the current request, or sends them now when no HTTP context is
    /// set or the request's buffer has already been sent. Nothing is sent or buffered for an empty list.
    /// </summary>
    private Task DeliverAsync(HubSend[] sends, CancellationToken ct)
    {
        if (sends.Length == 0)
        {
            return Task.CompletedTask;
        }

        HttpContext? context = httpContextAccessor.HttpContext;
        if (context is not null)
        {
            if (ct.IsCancellationRequested)
            {
                return Task.FromCanceled(ct);
            }

            if (TryBuffer(context, sends))
            {
                return Task.CompletedTask;
            }
        }

        return SendNowAsync(sends, ct);
    }

    /// <summary>
    /// Appends <paramref name="sends"/> to the buffer stored in <paramref name="context"/>, creating it and registering
    /// its completion callback on the first call of the request. Returns false, buffering nothing, when the buffer has
    /// already been sent.
    /// </summary>
    private bool TryBuffer(HttpContext context, HubSend[] sends)
    {
        lock (_gate)
        {
            if (context.Items.TryGetValue(DeferredSendsKey, out object? stored) && stored is DeferredSends existing)
            {
                if (existing.Sent)
                {
                    return false;
                }

                existing.Pending.AddRange(sends);
                return true;
            }

            var created = new DeferredSends();
            context.Response.OnCompleted(SendBufferedAsync, created);
            created.Pending.AddRange(sends);
            context.Items[DeferredSendsKey] = created;
            return true;
        }
    }

    /// <summary>Sends <paramref name="sends"/> in order, each send awaited before the next, with <paramref name="ct"/>.</summary>
    private async Task SendNowAsync(HubSend[] sends, CancellationToken ct)
    {
        foreach (HubSend send in sends)
        {
            await hub.Clients.Group(send.Group).SendAsync(send.Method, send.Payload, ct);
        }
    }

    /// <summary>
    /// The response-completed callback: sends the buffered messages of the <see cref="DeferredSends"/> in
    /// <paramref name="state"/> in order, logging each failed send, until the buffer is empty, and then marks it sent.
    /// </summary>
    private async Task SendBufferedAsync(object state)
    {
        var deferred = (DeferredSends)state;
        while (true)
        {
            HubSend[] batch;
            lock (_gate)
            {
                if (deferred.Pending.Count == 0)
                {
                    deferred.Sent = true;
                    return;
                }

                batch = deferred.Pending.ToArray();
                deferred.Pending.Clear();
            }

            foreach (HubSend send in batch)
            {
                try
                {
                    await hub.Clients.Group(send.Group).SendAsync(send.Method, send.Payload, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(ErrorOutcomeMiddleware.Sanitize(exception), DeferredSendFailedTemplate, send.Method);
                }
            }
        }
    }

    /// <summary>One hub message: the group it goes to, the hub method and the payload.</summary>
    /// <param name="Group">The hub group the message is sent to.</param>
    /// <param name="Method">The hub method the clients receive.</param>
    /// <param name="Payload">The message payload.</param>
    private sealed record HubSend(string Group, string Method, object Payload);

    /// <summary>The buffer of one request: the messages not yet sent, in call order, and whether it has been sent.</summary>
    private sealed class DeferredSends
    {
        /// <summary>The buffered messages, in call order.</summary>
        public List<HubSend> Pending { get; } = [];

        /// <summary>Whether the completion callback has sent every buffered message.</summary>
        public bool Sent { get; set; }
    }
}
