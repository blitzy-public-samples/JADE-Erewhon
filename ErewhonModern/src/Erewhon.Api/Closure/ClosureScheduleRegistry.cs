using System.Diagnostics.CodeAnalysis;
using Erewhon.Api.Sessions;

namespace Erewhon.Api.Closure;

/// <summary>
/// The tender-closure countdown of one session: the date tenders are closed at, the minutes per cycle and the
/// seconds left before the next closure.
/// </summary>
/// <param name="SessionId">The id of the session that started the schedule.</param>
/// <param name="Date">The date the next closure closes tenders at.</param>
/// <param name="Minutes">The minutes per cycle given when the schedule was started.</param>
/// <param name="SecondsToGo">The seconds left before the next closure.</param>
public sealed record ClosureSchedule(string SessionId, DateOnly Date, int Minutes, int SecondsToGo)
{
    /// <summary>
    /// The number of the <see cref="ClosureScheduleRegistry.Start"/> call that created the schedule: distinct for
    /// every successful call and increasing in call order within one registry.
    /// </summary>
    public long Generation { get; init; }
}

/// <summary>
/// Holds at most one tender-closure schedule per session in process memory, keyed by the ordinal session id.
/// A schedule lasts until <see cref="Stop"/>, a later <see cref="Start"/> for the same session, or the removal of
/// the session from the <see cref="SessionRegistry"/> ends it. Every member is safe to call from several threads at
/// once, and every returned <see cref="ClosureSchedule"/> is an immutable snapshot.
/// </summary>
public sealed class ClosureScheduleRegistry : IDisposable
{
    /// <summary>
    /// The number of seconds counted down per minute of a cycle.
    /// </summary>
    public const int SecondsPerMinute = 60;

    /// <summary>
    /// The number of seconds one timer tick counts down.
    /// </summary>
    public const int TimerSeconds = 1;

    private readonly object _gate = new();

    private readonly Dictionary<string, ClosureSchedule> _schedules = new(StringComparer.Ordinal);

    private readonly SessionRegistry _sessions;

    private long _generation;

    /// <summary>
    /// Creates an empty registry that stops the schedule of each session removed from <paramref name="sessions"/>.
    /// </summary>
    /// <param name="sessions">The registry whose <see cref="SessionRegistry.SessionRemoved"/> event stops schedules.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sessions"/> is null.</exception>
    public ClosureScheduleRegistry(SessionRegistry sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
        _sessions.SessionRemoved += OnSessionRemoved;
    }

    /// <summary>
    /// Removes any schedule of the session, then starts a new one at <paramref name="date"/> with
    /// <paramref name="minutes"/> × <see cref="SecondsPerMinute"/> seconds to go when that number is greater than 0.
    /// </summary>
    /// <param name="sessionId">The id of the session that owns the schedule.</param>
    /// <param name="date">The date the first closure closes tenders at.</param>
    /// <param name="minutes">The minutes per cycle.</param>
    /// <returns>
    /// <see langword="true"/> when a schedule was started; <see langword="false"/> when the seconds to go are 0 or
    /// less, in which case the session has no schedule.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    /// <exception cref="OverflowException">
    /// <paramref name="minutes"/> × <see cref="SecondsPerMinute"/> is outside the <see cref="int"/> range; the
    /// session's previous schedule has already been removed.
    /// </exception>
    public bool Start(string sessionId, DateOnly date, int minutes)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        lock (_gate)
        {
            _schedules.Remove(sessionId);

            var seconds = checked(minutes * SecondsPerMinute);
            if (seconds <= 0)
            {
                return false;
            }

            _schedules[sessionId] = new ClosureSchedule(sessionId, date, minutes, seconds)
            {
                Generation = ++_generation,
            };
            return true;
        }
    }

    /// <summary>
    /// Removes the schedule of the session.
    /// </summary>
    /// <param name="sessionId">The id of the session; null or empty removes nothing.</param>
    /// <returns><see langword="true"/> when the session had a schedule and it was removed; otherwise <see langword="false"/>.</returns>
    public bool Stop(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return false;
        }

        lock (_gate)
        {
            return _schedules.Remove(sessionId);
        }
    }

    /// <summary>
    /// Copies every schedule, ordered by <see cref="ClosureSchedule.Generation"/> ascending (start order).
    /// </summary>
    /// <returns>A new list that later changes to the registry do not affect.</returns>
    public IReadOnlyList<ClosureSchedule> Snapshot()
    {
        lock (_gate)
        {
            return _schedules.Values.OrderBy(schedule => schedule.Generation).ToArray();
        }
    }

    /// <summary>
    /// Looks up the schedule of the session.
    /// </summary>
    /// <param name="sessionId">The id of the session; null or empty finds nothing.</param>
    /// <param name="schedule">The schedule when found; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the session has a schedule; otherwise <see langword="false"/>.</returns>
    public bool TryGet(string? sessionId, [NotNullWhen(true)] out ClosureSchedule? schedule)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            schedule = null;
            return false;
        }

        lock (_gate)
        {
            return _schedules.TryGetValue(sessionId, out schedule);
        }
    }

    /// <summary>
    /// Stops reacting to <see cref="SessionRegistry.SessionRemoved"/>. Calling it more than once has no further effect.
    /// </summary>
    public void Dispose() => _sessions.SessionRemoved -= OnSessionRemoved;

    /// <summary>
    /// Stores and returns the schedule with <see cref="ClosureSchedule.SecondsToGo"/> reduced by
    /// <see cref="TimerSeconds"/>.
    /// </summary>
    /// <param name="sessionId">The id of the session.</param>
    /// <param name="generation">The <see cref="ClosureSchedule.Generation"/> the stored schedule must have.</param>
    /// <param name="updated">The stored schedule when counted down; otherwise <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="false"/>, with nothing changed, when the session has no schedule of that generation; otherwise
    /// <see langword="true"/>.
    /// </returns>
    internal bool TryCountDown(string sessionId, long generation, [NotNullWhen(true)] out ClosureSchedule? updated)
    {
        lock (_gate)
        {
            if (!TryGetGeneration(sessionId, generation, out var current))
            {
                updated = null;
                return false;
            }

            updated = current with { SecondsToGo = current.SecondsToGo - TimerSeconds };
            _schedules[sessionId] = updated;
            return true;
        }
    }

    /// <summary>
    /// Stores and returns the schedule with its <see cref="ClosureSchedule.Date"/> one day later and
    /// <see cref="ClosureSchedule.SecondsToGo"/> reset to <see cref="ClosureSchedule.Minutes"/> ×
    /// <see cref="SecondsPerMinute"/>, keeping its <see cref="ClosureSchedule.Generation"/>. A schedule whose date is
    /// <see cref="DateOnly.MaxValue"/> is removed instead.
    /// </summary>
    /// <param name="sessionId">The id of the session.</param>
    /// <param name="generation">The <see cref="ClosureSchedule.Generation"/> the stored schedule must have.</param>
    /// <param name="restarted">The stored schedule when restarted; otherwise <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="false"/> when the session has no schedule of that generation, with nothing changed, or when the
    /// schedule's date is <see cref="DateOnly.MaxValue"/> and the schedule was removed; otherwise <see langword="true"/>.
    /// </returns>
    internal bool TryCompleteCycle(string sessionId, long generation, [NotNullWhen(true)] out ClosureSchedule? restarted)
    {
        lock (_gate)
        {
            if (!TryGetGeneration(sessionId, generation, out var current))
            {
                restarted = null;
                return false;
            }

            if (current.Date == DateOnly.MaxValue)
            {
                _schedules.Remove(sessionId);
                restarted = null;
                return false;
            }

            restarted = current with
            {
                Date = current.Date.AddDays(1),
                SecondsToGo = current.Minutes * SecondsPerMinute,
            };
            _schedules[sessionId] = restarted;
            return true;
        }
    }

    /// <summary>
    /// Removes the schedule of the session when it has the given generation.
    /// </summary>
    /// <param name="sessionId">The id of the session.</param>
    /// <param name="generation">The <see cref="ClosureSchedule.Generation"/> the stored schedule must have.</param>
    /// <returns><see langword="true"/> when a schedule of that generation was removed; otherwise <see langword="false"/>.</returns>
    internal bool Remove(string sessionId, long generation)
    {
        lock (_gate)
        {
            return TryGetGeneration(sessionId, generation, out _) && _schedules.Remove(sessionId);
        }
    }

    private bool TryGetGeneration(string? sessionId, long generation, [NotNullWhen(true)] out ClosureSchedule? schedule)
    {
        if (!string.IsNullOrEmpty(sessionId)
            && _schedules.TryGetValue(sessionId, out var stored)
            && stored.Generation == generation)
        {
            schedule = stored;
            return true;
        }

        schedule = null;
        return false;
    }

    private void OnSessionRemoved(string sessionId) => Stop(sessionId);
}
