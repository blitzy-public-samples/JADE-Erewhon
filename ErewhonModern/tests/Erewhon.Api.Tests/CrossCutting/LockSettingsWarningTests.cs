using Erewhon.Persistence;
using Erewhon.Testing;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;

namespace Erewhon.Api.Tests.CrossCutting;

/// <summary>
/// Characterises the start-up handling of the two lock waits, <c>Erewhon:Locks:TimeoutMs</c> and
/// <c>Erewhon:Locks:AllocationTimeoutMs</c>. Each case builds and starts the real API on Kestrel over the assembly
/// database with both values replaced, reads the <see cref="DependencyInjection.LockSettingRejectedEventId"/> events
/// the host's Serilog logger received by the time the host listens, and then the <see cref="EntityLockSettings"/> the
/// host applies.
/// </summary>
public sealed class LockSettingsWarningTests
{
    private const string LockTimeoutKey = "Erewhon:Locks:TimeoutMs";

    private const string AllocationTimeoutKey = "Erewhon:Locks:AllocationTimeoutMs";

    private const int DefaultLockWaitMs = 10000;

    private const string RejectedTemplate =
        "{ConfigurationKey} is {RejectedValue}, which is not a positive integer number of milliseconds; "
        + "{LockWaitMs} ms is used instead";

    private const string RejectedSourceContext = "Erewhon.Persistence.EntityLockSettings";

    /// <summary>
    /// The value of each key (<see langword="null"/> for absent), the waits the host applies, and the keys reported
    /// as rejected, in order.
    /// </summary>
    public static TheoryData<string?, string?, int, int, string[]> Cases => new()
    {
        { "abc", "xyz", DefaultLockWaitMs, DefaultLockWaitMs, [LockTimeoutKey, AllocationTimeoutKey] },
        { "0", "1500", DefaultLockWaitMs, 1500, [LockTimeoutKey] },
        { "1500", "-5", 1500, DefaultLockWaitMs, [AllocationTimeoutKey] },
        { "1.5", "300", DefaultLockWaitMs, 300, [LockTimeoutKey] },
        { "2147483648", null, DefaultLockWaitMs, DefaultLockWaitMs, [LockTimeoutKey] },
        { " ", "", DefaultLockWaitMs, DefaultLockWaitMs, [LockTimeoutKey] },
        { "1500", "300", 1500, 300, [] },
        { null, null, DefaultLockWaitMs, DefaultLockWaitMs, [] },
    };

    /// <summary>
    /// A value that is present and not empty but is not a positive integer gives 10000 and one
    /// <see cref="LogEventLevel.Warning"/> event naming its key, its value and the 10000 ms used, written while the
    /// host is built and not again; a positive integer is applied, and an absent or empty value gives 10000, both
    /// without an event.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RejectedLockSettingIsLoggedOnceAtStartUp(
        string? lockTimeout,
        string? allocationTimeout,
        int expectedLockTimeoutMs,
        int expectedAllocationTimeoutMs,
        string[] expectedRejectedKeys)
    {
        var values = new Dictionary<string, string?>
        {
            [LockTimeoutKey] = lockTimeout,
            [AllocationTimeoutKey] = allocationTimeout,
        };
        var sink = new CollectingLogSink();
        PostgresDatabase database = PostgresDatabase.ForAssembly(typeof(LockSettingsWarningTests).Assembly);
        await database.EnsureCreatedAndMigratedAsync();

        await using ErewhonApiHost host = await ErewhonApiHost.StartAsync(new ErewhonApiHostOptions
        {
            ConnectionString = database.ConnectionString,
            Settings = values,
            LogSink = sink,
        });

        LogEvent[] atStartUp = RejectedEvents(sink);
        Assert.Equal(expectedRejectedKeys.Length, atStartUp.Length);
        for (var index = 0; index < expectedRejectedKeys.Length; index++)
        {
            string key = expectedRejectedKeys[index];
            LogEvent warning = atStartUp[index];
            Assert.Equal(LogEventLevel.Warning, warning.Level);
            Assert.Equal(RejectedTemplate, warning.MessageTemplate.Text);
            Assert.Equal(new ScalarValue(RejectedSourceContext), warning.Properties["SourceContext"]);
            Assert.Equal(new ScalarValue(key), warning.Properties["ConfigurationKey"]);
            Assert.Equal(new ScalarValue(values[key]), warning.Properties["RejectedValue"]);
            Assert.Equal(new ScalarValue(DefaultLockWaitMs), warning.Properties["LockWaitMs"]);
        }

        EntityLockSettings applied = host.Services.GetRequiredService<EntityLockSettings>();
        Assert.Equal(expectedLockTimeoutMs, applied.LockTimeoutMs);
        Assert.Equal(expectedAllocationTimeoutMs, applied.AllocationTimeoutMs);
        Assert.Equal(atStartUp, RejectedEvents(sink));
    }

    /// <summary>
    /// Returns the collected events whose event id and event name are those of
    /// <see cref="DependencyInjection.LockSettingRejectedEventId"/>, 600 <c>ErewhonLockSettingRejected</c>, in emission
    /// order.
    /// </summary>
    private static LogEvent[] RejectedEvents(CollectingLogSink sink) =>
        sink.Events
            .Where(logEvent =>
                Equals(EventIdPart(logEvent, "Id"), new ScalarValue(600))
                && Equals(EventIdPart(logEvent, "Name"), new ScalarValue("ErewhonLockSettingRejected")))
            .ToArray();

    /// <summary>Returns the named part of the event's <c>EventId</c> property, or <see langword="null"/>.</summary>
    private static LogEventPropertyValue? EventIdPart(LogEvent logEvent, string part) =>
        logEvent.Properties.TryGetValue("EventId", out LogEventPropertyValue? eventId) && eventId is StructureValue structure
            ? structure.Properties.FirstOrDefault(property => property.Name == part)?.Value
            : null;
}
