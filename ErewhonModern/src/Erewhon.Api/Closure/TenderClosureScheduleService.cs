using Erewhon.Api.Errors;
using Erewhon.Api.Realtime;
using Erewhon.Application.Services;
using Erewhon.Persistence;
using Microsoft.AspNetCore.SignalR;

namespace Erewhon.Api.Closure;

/// <summary>
/// Background service that runs the tender-closure schedules held in <see cref="ClosureScheduleRegistry"/>. Every
/// <see cref="TickMsKey"/> milliseconds it counts each schedule down by one timer second and sends
/// <see cref="ErewhonHub.ClosureTick"/> to the scheduling session; when a schedule reaches zero it closes the bound
/// company's tenders at the schedule's date through <see cref="TenderClosureService.CloseTendersAtDateAsync"/>, sends
/// <see cref="ErewhonHub.ClosureCompleted"/>, and restarts the schedule one day later with its full countdown.
/// Ports the timer of <c>ErewhonCloseTendersForm</c> (<c>timerEvent</c> and <c>startTimer</c>).
/// </summary>
/// <param name="registry">The registry whose schedules are counted down, restarted and removed.</param>
/// <param name="scopes">The factory of the dependency-injection scope each closure runs in.</param>
/// <param name="hub">The hub context that sends the tick and completion messages to the session groups.</param>
/// <param name="configuration">The configuration that supplies <see cref="TickMsKey"/>.</param>
/// <param name="logger">The logger that receives closure failures, send failures and tick failures.</param>
public sealed class TenderClosureScheduleService(
    ClosureScheduleRegistry registry,
    IServiceScopeFactory scopes,
    IHubContext<ErewhonHub> hub,
    IConfiguration configuration,
    ILogger<TenderClosureScheduleService> logger) : BackgroundService
{
    /// <summary>The configuration key of the timer period in milliseconds.</summary>
    public const string TickMsKey = "Erewhon:Closure:TickMs";

    /// <summary>The timer period in milliseconds used when <see cref="TickMsKey"/> is missing, 0 or negative.</summary>
    public const int DefaultTickMs = 1000;

    private readonly ClosureScheduleRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    private readonly IServiceScopeFactory _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));

    private readonly IHubContext<ErewhonHub> _hub = hub ?? throw new ArgumentNullException(nameof(hub));

    private readonly IConfiguration _configuration =
        configuration ?? throw new ArgumentNullException(nameof(configuration));

    private readonly ILogger<TenderClosureScheduleService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Reads the timer period from <see cref="TickMsKey"/> once, then runs <see cref="TickAsync"/> on every period
    /// until <paramref name="stoppingToken"/> is cancelled. A tick that throws is logged as an error event with the
    /// rendering of <see cref="ErrorOutcomeMiddleware.Sanitize"/> attached, and the next period runs as usual;
    /// cancellation ends the loop without logging an error.
    /// </summary>
    /// <param name="stoppingToken">Token that is cancelled when the host stops.</param>
    /// <returns>A task that completes when the host stops.</returns>
    /// <exception cref="InvalidOperationException">The <see cref="TickMsKey"/> value is not an integer.</exception>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tickMs = ReadTickMs();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(tickMs));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _logger.LogError(ErrorOutcomeMiddleware.Sanitize(exception), "Tender closure schedule tick failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
    }

    /// <summary>
    /// Runs one timer second for every schedule in <see cref="ClosureScheduleRegistry.Snapshot"/>, one schedule after
    /// another in start order. A schedule stopped or replaced since the snapshot is skipped. Each other schedule is
    /// counted down by one second; while seconds remain, its session receives <see cref="ErewhonHub.ClosureTick"/>
    /// with a <see cref="ClosureTickMessage"/> holding them. A schedule that reaches zero closes the bound company's
    /// tenders at its date. When the closure succeeds and the schedule is still the same one, the schedule restarts
    /// one day later with <see cref="ClosureSchedule.Minutes"/> × <see cref="ClosureScheduleRegistry.SecondsPerMinute"/>
    /// seconds to go, and its session receives <see cref="ErewhonHub.ClosureCompleted"/> with a
    /// <see cref="ClosureCompletedMessage"/> holding the date closed and the number of tender items awarded, then
    /// <see cref="ErewhonHub.ClosureTick"/> with the restarted countdown. When the closure throws, the schedule is
    /// removed, nothing more is sent to its session, and one error event is logged through
    /// <see cref="ErrorOutcomeMiddleware.LogOutcome"/> with <c>ClosureDate</c> in the log scope, except for a lock
    /// outcome, which logs nothing; the awards committed before the failure stay. A send that fails is logged as a
    /// warning event with the hub method as <c>HubMethod</c> and the rendering of
    /// <see cref="ErrorOutcomeMiddleware.Sanitize"/> attached, and the schedule continues. No event carries the
    /// session id. A failure of one schedule leaves the others running.
    /// Ports <c>ErewhonCloseTendersForm.timerEvent</c>.
    /// </summary>
    /// <param name="ct">Token that cancels the tick.</param>
    /// <returns>A task that completes when every schedule has had its timer second.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task TickAsync(CancellationToken ct)
    {
        foreach (var schedule in _registry.Snapshot())
        {
            ct.ThrowIfCancellationRequested();

            if (!_registry.TryCountDown(schedule.SessionId, schedule.Generation, out var counted))
            {
                continue;
            }

            if (counted.SecondsToGo > 0)
            {
                await SendAsync(
                    counted.SessionId,
                    ErewhonHub.ClosureTick,
                    new ClosureTickMessage(counted.SecondsToGo),
                    ct);
                continue;
            }

            await CloseAsync(counted, ct);
        }
    }

    private int ReadTickMs()
    {
        var configured = _configuration.GetValue<int?>(TickMsKey);
        return configured is > 0 ? configured.Value : DefaultTickMs;
    }

    private async Task CloseAsync(ClosureSchedule counted, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();

        int numClosed;
        try
        {
            var closure = scope.ServiceProvider.GetRequiredService<TenderClosureService>();
            numClosed = await closure.CloseTendersAtDateAsync(counted.Date, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _registry.Remove(counted.SessionId, counted.Generation);

            var mapper = scope.ServiceProvider.GetService<PostgresErrorMapper>();
            var translated = ErrorOutcomeMiddleware.Translate(exception, mapper);
            using (_logger.BeginScope(new Dictionary<string, object>
            {
                ["ClosureDate"] = counted.Date,
            }))
            {
                if (ErrorOutcomeMiddleware.IsLogged(translated))
                {
                    ErrorOutcomeMiddleware.LogOutcome(_logger, translated, null);
                }
            }

            return;
        }

        if (!_registry.TryCompleteCycle(counted.SessionId, counted.Generation, out var restarted))
        {
            return;
        }

        await SendAsync(
            counted.SessionId,
            ErewhonHub.ClosureCompleted,
            new ClosureCompletedMessage(counted.Date, numClosed),
            ct);
        await SendAsync(
            counted.SessionId,
            ErewhonHub.ClosureTick,
            new ClosureTickMessage(restarted.SecondsToGo),
            ct);
    }

    private async Task SendAsync(string sessionId, string method, object payload, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.Group(ErewhonHub.SessionGroup(sessionId)).SendAsync(method, payload, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                ErrorOutcomeMiddleware.Sanitize(exception),
                "Could not send {HubMethod} to the scheduling session",
                method);
        }
    }
}
