using Erewhon.Application.Notifications;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Erewhon.Persistence;

/// <summary>
/// Feeds the <see cref="NotificationDeriver"/> from the saves of the context it is attached to: before each save runs it
/// hands the deriver every tracked entry of that context, after the save succeeds it has the deriver enqueue the captured
/// changes, and when the save fails or is canceled it has the deriver drop them. It does no filtering of its own, never
/// flushes the notification queue and never publishes.
/// One instance serves one dependency-injection scope, together with the context and the deriver of that scope.
/// </summary>
/// <param name="deriver">The deriver that captures the saved rows and enqueues their change events.</param>
public sealed class ChangeNotificationInterceptor(NotificationDeriver deriver) : SaveChangesInterceptor
{
    private readonly NotificationDeriver _deriver = deriver ?? throw new ArgumentNullException(nameof(deriver));

    /// <summary>
    /// Passes the tracked entries of the context about to be saved to <see cref="NotificationDeriver.RecordSavingChanges"/>,
    /// or drops any pending capture when the event carries no context.
    /// </summary>
    /// <param name="eventData">The context being saved.</param>
    /// <param name="result">The interception result so far.</param>
    /// <returns><paramref name="result"/>, unchanged.</returns>
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData);
        return result;
    }

    /// <summary>
    /// Passes the tracked entries of the context about to be saved to <see cref="NotificationDeriver.RecordSavingChanges"/>,
    /// or drops any pending capture when the event carries no context.
    /// </summary>
    /// <param name="eventData">The context being saved.</param>
    /// <param name="result">The interception result so far.</param>
    /// <param name="cancellationToken">Not observed; the capture completes synchronously.</param>
    /// <returns><paramref name="result"/>, unchanged.</returns>
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Capture(eventData);
        return ValueTask.FromResult(result);
    }

    /// <summary>Enqueues the captured changes through <see cref="NotificationDeriver.CompleteSavedChanges"/>.</summary>
    /// <param name="eventData">The completed save.</param>
    /// <param name="result">The number of state entries written to the database.</param>
    /// <returns><paramref name="result"/>, unchanged.</returns>
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        _deriver.CompleteSavedChanges();
        return result;
    }

    /// <summary>Enqueues the captured changes through <see cref="NotificationDeriver.CompleteSavedChanges"/>.</summary>
    /// <param name="eventData">The completed save.</param>
    /// <param name="result">The number of state entries written to the database.</param>
    /// <param name="cancellationToken">Not observed; the enqueue completes synchronously.</param>
    /// <returns><paramref name="result"/>, unchanged.</returns>
    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        _deriver.CompleteSavedChanges();
        return ValueTask.FromResult(result);
    }

    /// <summary>
    /// Drops the captured changes through <see cref="NotificationDeriver.DiscardPendingCapture"/>; the save's exception
    /// propagates unchanged.
    /// </summary>
    /// <param name="eventData">The failed save and its exception.</param>
    public override void SaveChangesFailed(DbContextErrorEventData eventData) => _deriver.DiscardPendingCapture();

    /// <summary>
    /// Drops the captured changes through <see cref="NotificationDeriver.DiscardPendingCapture"/>; the save's exception
    /// propagates unchanged.
    /// </summary>
    /// <param name="eventData">The failed save and its exception.</param>
    /// <param name="cancellationToken">Not observed; the discard completes synchronously.</param>
    /// <returns>A completed task.</returns>
    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _deriver.DiscardPendingCapture();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Drops the captured changes through <see cref="NotificationDeriver.DiscardPendingCapture"/> when the concurrency
    /// exception of the save is about to be thrown, i.e. when <paramref name="result"/> is not suppressed.
    /// </summary>
    /// <param name="eventData">The save and the entries whose update or delete affected no row.</param>
    /// <param name="result">The interception result so far.</param>
    /// <returns><paramref name="result"/>, unchanged.</returns>
    public override InterceptionResult ThrowingConcurrencyException(ConcurrencyExceptionEventData eventData, InterceptionResult result)
    {
        DiscardUnlessSuppressed(result);
        return result;
    }

    /// <summary>
    /// Drops the captured changes through <see cref="NotificationDeriver.DiscardPendingCapture"/> when the concurrency
    /// exception of the save is about to be thrown, i.e. when <paramref name="result"/> is not suppressed.
    /// </summary>
    /// <param name="eventData">The save and the entries whose update or delete affected no row.</param>
    /// <param name="result">The interception result so far.</param>
    /// <param name="cancellationToken">Not observed; the discard completes synchronously.</param>
    /// <returns><paramref name="result"/>, unchanged.</returns>
    public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        DiscardUnlessSuppressed(result);
        return ValueTask.FromResult(result);
    }

    /// <summary>Drops the captured changes through <see cref="NotificationDeriver.DiscardPendingCapture"/>.</summary>
    /// <param name="eventData">The canceled save.</param>
    public override void SaveChangesCanceled(DbContextEventData eventData) => _deriver.DiscardPendingCapture();

    /// <summary>Drops the captured changes through <see cref="NotificationDeriver.DiscardPendingCapture"/>.</summary>
    /// <param name="eventData">The canceled save.</param>
    /// <param name="cancellationToken">Not observed; the discard completes synchronously.</param>
    /// <returns>A completed task.</returns>
    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        _deriver.DiscardPendingCapture();
        return Task.CompletedTask;
    }

    private void Capture(DbContextEventData eventData)
    {
        if (eventData.Context is null)
        {
            _deriver.DiscardPendingCapture();
            return;
        }

        var entries = eventData.Context.ChangeTracker.Entries().ToList();
        _deriver.RecordSavingChanges(entries);
    }

    private void DiscardUnlessSuppressed(InterceptionResult result)
    {
        if (!result.IsSuppressed)
        {
            _deriver.DiscardPendingCapture();
        }
    }
}
