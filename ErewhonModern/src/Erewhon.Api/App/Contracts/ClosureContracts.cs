namespace Erewhon.Api.App.Contracts;

/// <summary>
/// State of the close-tenders dialog for the caller's session, replacing the values
/// <c>ErewhonCloseTendersForm.formLoad</c> sets and the timer state <c>startTimer</c> and <c>stopTimer</c> keep.
/// </summary>
/// <param name="ServerDate">The database server date, the initial value of <c>txtCloseDate</c>.</param>
/// <param name="Running">True when a closure schedule is active for the session, as when <c>btnStop</c> is enabled and <c>btnStart</c> disabled.</param>
/// <param name="Date">The date the next scheduled closure uses, the current <c>txtCloseDate</c> of the running schedule; null when no schedule is running.</param>
/// <param name="Minutes">The minutes entered in <c>txtMinutes</c> when the schedule started; null when no schedule is running.</param>
/// <param name="SecondsToGo">The seconds left before the next scheduled closure, the form's <c>secondsToGo</c> shown in <c>lblSecondsToGo</c>; null when no schedule is running.</param>
public sealed record ClosureScheduleStateResponse(
    DateOnly ServerDate,
    bool Running,
    DateOnly? Date,
    int? Minutes,
    int? SecondsToGo);

/// <summary>
/// Request of the "Close now" action, replacing <c>ErewhonCloseTendersForm.btnCloseNow_click</c>.
/// </summary>
/// <param name="Date">The date tenders are closed at, the value of <c>txtCloseDate</c>; a missing date binds the null date 0001-01-01, which the route refuses with 400.</param>
public sealed record CloseNowRequest(DateOnly Date);

/// <summary>
/// Result of the "Close now" action, replacing the caption <c>btnCloseNow_click</c> writes to <c>lblNumClosedNow</c>.
/// </summary>
/// <param name="NumClosed">The number of tender items awarded, the <c>numTendersClosed</c> output of <c>closeTendersAtDate</c>.</param>
public sealed record CloseNowResponse(int NumClosed);

/// <summary>
/// Request that starts the closure schedule for the caller's session, replacing
/// <c>ErewhonCloseTendersForm.btnStart_click</c> and <c>startTimer(false)</c>.
/// </summary>
/// <param name="Date">The date the first scheduled closure uses, the value of <c>txtCloseDate</c>; a missing date binds the null date 0001-01-01, which the route refuses with 400.</param>
/// <param name="Minutes">The minutes between scheduled closures, the value of <c>txtMinutes</c>.</param>
public sealed record StartClosureScheduleRequest(DateOnly Date, int Minutes);

/// <summary>
/// Result of starting the closure schedule, replacing the button and label state <c>startTimer(false)</c> sets.
/// </summary>
/// <param name="Started">True when the schedule started, as when <c>startTimer</c> begins the timer, disables <c>btnStart</c> and enables <c>btnStop</c>; false when <c>secondsToGo</c> is not greater than 0, or when the session was logged out before the schedule could start.</param>
/// <param name="SecondsToGo">The initial <c>secondsToGo</c>, the minutes multiplied by 60, shown in <c>lblSecondsToGo</c> while the schedule runs.</param>
public sealed record StartClosureScheduleResponse(bool Started, int SecondsToGo);

/// <summary>
/// Result of stopping the closure schedule, replacing <c>ErewhonCloseTendersForm.btnStop_click</c> and <c>stopTimer</c>.
/// </summary>
/// <param name="Stopped">True when a running schedule for the session was removed; false when none was running.</param>
public sealed record StopClosureScheduleResponse(bool Stopped);
