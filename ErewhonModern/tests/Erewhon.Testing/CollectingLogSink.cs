using Serilog.Core;
using Serilog.Events;

namespace Erewhon.Testing;

/// <summary>
/// A Serilog sink that keeps every event it receives in memory, in the order received. Safe to call from any
/// number of threads at once.
/// </summary>
public sealed class CollectingLogSink : ILogEventSink
{
    private readonly object _gate = new();
    private readonly List<LogEvent> _events = new();

    /// <summary>
    /// A snapshot of every event received since creation or the last <see cref="Clear"/>, in emission order.
    /// </summary>
    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    /// <summary>
    /// A snapshot of the received events whose level is <see cref="LogEventLevel.Error"/> or
    /// <see cref="LogEventLevel.Fatal"/>, in emission order.
    /// </summary>
    public IReadOnlyList<LogEvent> ErrorEvents
    {
        get
        {
            lock (_gate)
            {
                return _events
                    .Where(logEvent => logEvent.Level is LogEventLevel.Error or LogEventLevel.Fatal)
                    .ToArray();
            }
        }
    }

    /// <summary>Appends <paramref name="logEvent"/> to the collected events.</summary>
    /// <param name="logEvent">The event to keep.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logEvent"/> is null.</exception>
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        lock (_gate)
        {
            _events.Add(logEvent);
        }
    }

    /// <summary>Removes every collected event.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _events.Clear();
        }
    }
}
