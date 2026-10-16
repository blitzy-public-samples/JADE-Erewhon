using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Erewhon.Api.Tests.Startup;

/// <summary>
/// Characterises the console log of the Erewhon host and the end of a process whose host fails. A host whose
/// configuration has no <c>Serilog:WriteTo</c> entry writes each event once as compact JSON to standard output, with
/// the level overrides of the base <c>appsettings.json</c>, and a host with the committed configuration writes each event
/// once through its configured sink. A failure to build or start the host, for the web server and for a command-line
/// verb, ends <see cref="ErewhonHost.RunAsync"/> with exit code 1 and one fatal <c>ErewhonHostFailed</c> event on
/// standard output instead of an exception. A <see cref="HostAbortedException"/> thrown from the <c>HostBuilt</c>
/// diagnostic event while the host is built, as the EF Core design-time tools throw it, propagates out of
/// <see cref="ErewhonHost.RunAsync"/> with no fatal and no <c>ErewhonHostFailed</c> event. Standard output is
/// captured by replacing <see cref="Console.Out"/> while each case runs.
/// </summary>
public sealed class HostLoggingTests
{
    private const string ProbeCategory = "Erewhon.Api.Tests.Startup.HostLoggingTests";

    private const string HostingDiagnosticsCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

    private const string FatalLevelMarker = "\"@l\":\"Fatal\"";

    private const string HostFailedEventName = "ErewhonHostFailed";

    /// <summary>
    /// Whether a <see cref="HostBuiltEventObserver"/> aborts a host whose <c>HostBuilt</c> event is written in the
    /// current execution context.
    /// </summary>
    private static readonly AsyncLocal<bool> HostBuiltAbortRequested = new();

    /// <summary>
    /// Builds the host with the test output folder as its content root, which holds the committed
    /// <c>appsettings.json</c>, or with an empty folder as its content root, and writes Information and Warning events
    /// through its logger factory. Each written event is one compact JSON line on standard output; Information events
    /// of the sources the base file sets to <c>Warning</c> are not written.
    /// </summary>
    /// <param name="emptyContentRoot">Whether the content root is an empty folder.</param>
    /// <param name="suppressedCategories">The categories whose Information events are not written.</param>
    [Theory]
    [InlineData(
        false,
        new[] { HostingDiagnosticsCategory })]
    [InlineData(
        true,
        new[] { "Microsoft.AspNetCore.Routing", HostingDiagnosticsCategory, "Microsoft.EntityFrameworkCore.Database.Command" })]
    public async Task HostLoggerWritesEachEventOnceAsCompactJson(bool emptyContentRoot, string[] suppressedCategories)
    {
        var marker = Guid.NewGuid().ToString("N");
        var contentRoot = emptyContentRoot ? Directory.CreateTempSubdirectory("erewhon-host-logging-").FullName : null;
        try
        {
            var (hasAppSettings, lines) = await CaptureStandardOutputAsync(async () =>
            {
                await using var app = ErewhonHost.Build(contentRoot is null ? [] : ["--contentRoot", contentRoot]);
                var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
                loggerFactory.CreateLogger(ProbeCategory).LogInformation("Probe information {Marker}", marker);
                foreach (var category in suppressedCategories)
                {
                    loggerFactory.CreateLogger(category).LogInformation("Suppressed information {Marker}", marker);
                }

                loggerFactory.CreateLogger(HostingDiagnosticsCategory).LogWarning("Hosting warning {Marker}", marker);
                return File.Exists(Path.Combine(app.Environment.ContentRootPath, "appsettings.json"));
            });

            Assert.Equal(!emptyContentRoot, hasAppSettings);
            var written = lines
                .Where(line => line.Contains(marker, StringComparison.Ordinal))
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .ToArray();
            Assert.Equal(
                new[] { "Probe information {Marker}", "Hosting warning {Marker}" },
                written.Select(logEvent => logEvent.GetProperty("@mt").GetString()));
            Assert.Equal(
                new[] { ProbeCategory, HostingDiagnosticsCategory },
                written.Select(logEvent => logEvent.GetProperty("SourceContext").GetString()));
        }
        finally
        {
            if (contentRoot is not null)
            {
                Directory.Delete(contentRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// Runs <see cref="ErewhonHost.RunAsync"/> with one invalid setting: given on the command line of the web server,
    /// or as an environment variable of the <c>migrate</c> verb, whose host reads no command line. A tick that is not an
    /// integer fails the start of the hosted closure service; a Serilog level that is not a level fails the build of the
    /// host's logger. The process ends with exit code 1 and standard output holds one fatal event, category
    /// <c>Erewhon.Api.ErewhonHost</c>, event id 1 <c>ErewhonHostFailed</c>, <c>ExitCode</c> 1, whose exception
    /// names the failure.
    /// </summary>
    /// <param name="verb">The verb to run, or <see langword="null"/> for the web server.</param>
    /// <param name="key">The configuration key of the invalid setting.</param>
    /// <param name="value">The invalid value.</param>
    /// <param name="expectedFailure">Text of the exception that the fatal event carries.</param>
    [Theory]
    [InlineData(null, "Erewhon:Closure:TickMs", "abc", "Failed to convert configuration value at 'Erewhon:Closure:TickMs'")]
    [InlineData(null, "Serilog:MinimumLevel:Default", "abc", "The value abc is not a valid Serilog level.")]
    [InlineData("migrate", "Serilog:MinimumLevel:Default", "abc", "The value abc is not a valid Serilog level.")]
    public async Task HostFailureWritesOneFatalEventAndReturnsOne(
        string? verb,
        string key,
        string value,
        string expectedFailure)
    {
        var environmentVariable = key.Replace(":", "__", StringComparison.Ordinal);
        var previousValue = Environment.GetEnvironmentVariable(environmentVariable);
        string[] args = verb is null ? ["--urls", "http://127.0.0.1:0", $"--{key}={value}"] : [verb];
        if (verb is not null)
        {
            Environment.SetEnvironmentVariable(environmentVariable, value);
        }

        int exitCode;
        string[] lines;
        try
        {
            (exitCode, lines) = await CaptureStandardOutputAsync(() => ErewhonHost.RunAsync(args));
        }
        finally
        {
            if (verb is not null)
            {
                Environment.SetEnvironmentVariable(environmentVariable, previousValue);
            }
        }

        Assert.Equal(1, exitCode);
        var fatal = JsonSerializer.Deserialize<JsonElement>(
            Assert.Single(lines, line => line.Contains(FatalLevelMarker, StringComparison.Ordinal)));
        Assert.Equal("Erewhon.Api.ErewhonHost", fatal.GetProperty("SourceContext").GetString());
        Assert.Equal(1, fatal.GetProperty("EventId").GetProperty("Id").GetInt32());
        Assert.Equal("ErewhonHostFailed", fatal.GetProperty("EventId").GetProperty("Name").GetString());
        Assert.Equal(1, fatal.GetProperty("ExitCode").GetInt32());
        Assert.Contains(expectedFailure, fatal.GetProperty("@x").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs <see cref="ErewhonHost.RunAsync"/> while a <see cref="HostBuiltAbortingObserver"/> observes the
    /// host-building events as the EF Core design-time tools do: on the <c>HostBuilt</c> event it takes the built host
    /// and throws a <see cref="HostAbortedException"/>. For the web server and for the <c>migrate</c> verb, that
    /// exception propagates out of <see cref="ErewhonHost.RunAsync"/> as thrown, and standard output holds no fatal
    /// event and no <c>ErewhonHostFailed</c> event. The taken host is disposed afterwards.
    /// </summary>
    /// <param name="verb">The verb to run, or <see langword="null"/> for the web server.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("migrate")]
    public async Task HostAbortedWhileBuiltPropagatesWithoutFatalEvent(string? verb)
    {
        string[] args = verb is null ? ["--urls", "http://127.0.0.1:0"] : [verb];
        var observer = new HostBuiltAbortingObserver();
        HostAbortedException aborted;
        string[] lines;
        HostBuiltAbortRequested.Value = true;
        try
        {
            (aborted, lines) = await CaptureStandardOutputAsync(
                () => Assert.ThrowsAsync<HostAbortedException>(() => ErewhonHost.RunAsync(args)));
        }
        finally
        {
            HostBuiltAbortRequested.Value = false;
            observer.Dispose();
            if (observer.BuiltHost is IAsyncDisposable asyncDisposableHost)
            {
                await asyncDisposableHost.DisposeAsync();
            }
            else
            {
                observer.BuiltHost?.Dispose();
            }
        }

        Assert.NotNull(observer.BuiltHost);
        Assert.Same(observer.AbortException, aborted);
        Assert.DoesNotContain(lines, line => line.Contains(FatalLevelMarker, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(HostFailedEventName, StringComparison.Ordinal));
    }

    /// <summary>
    /// Runs <paramref name="action"/> with <see cref="Console.Out"/> replaced by an in-memory writer, restores the
    /// previous writer, and returns the result with the captured text split into non-empty lines.
    /// </summary>
    /// <typeparam name="T">The type of the action's result.</typeparam>
    /// <param name="action">The work whose standard output is captured.</param>
    /// <returns>The action's result and the captured lines.</returns>
    private static async Task<(T Result, string[] Lines)> CaptureStandardOutputAsync<T>(Func<Task<T>> action)
    {
        var original = Console.Out;
        using var buffer = new StringWriter();
        var capture = TextWriter.Synchronized(buffer);
        Console.SetOut(capture);
        T result;
        try
        {
            result = await action();
        }
        finally
        {
            Console.SetOut(original);
        }

        string text;
        lock (capture)
        {
            text = buffer.ToString();
        }

        return (result, text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>
    /// Subscribes to <see cref="DiagnosticListener.AllListeners"/> when created and, for every listener named
    /// <c>Microsoft.Extensions.Hosting</c> that exists or is created while subscribed, subscribes a
    /// <see cref="HostBuiltEventObserver"/>. Every subscription is disposed by <see cref="Dispose"/>, or when the
    /// sequence of listeners ends; a listener reported after that gets no subscription.
    /// </summary>
    private sealed class HostBuiltAbortingObserver : IObserver<DiagnosticListener>, IDisposable
    {
        private const string HostingListenerName = "Microsoft.Extensions.Hosting";

        private readonly object gate = new();

        private readonly List<IDisposable> subscriptions = [];

        private bool disposed;

        /// <summary>Subscribes the new observer to <see cref="DiagnosticListener.AllListeners"/>.</summary>
        public HostBuiltAbortingObserver() => Track(DiagnosticListener.AllListeners.Subscribe(this));

        /// <summary>Gets the host taken from the <c>HostBuilt</c> event that was aborted, if any.</summary>
        public IHost? BuiltHost { get; private set; }

        /// <summary>Gets the exception thrown from the <c>HostBuilt</c> event that was aborted, if any.</summary>
        public HostAbortedException? AbortException { get; private set; }

        /// <summary>
        /// Subscribes a <see cref="HostBuiltEventObserver"/> to <paramref name="listener"/> when it is named
        /// <c>Microsoft.Extensions.Hosting</c>.
        /// </summary>
        /// <param name="listener">A listener that exists or has been created.</param>
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == HostingListenerName)
            {
                Track(new HostBuiltEventObserver(listener, this));
            }
        }

        /// <summary>Disposes every subscription.</summary>
        public void OnCompleted() => Dispose();

        /// <summary>Disposes every subscription.</summary>
        /// <param name="error">The error that ended the sequence of listeners.</param>
        public void OnError(Exception error) => Dispose();

        /// <summary>
        /// Disposes every subscription, the one to <see cref="DiagnosticListener.AllListeners"/> included.
        /// </summary>
        public void Dispose()
        {
            IDisposable[] released;
            lock (gate)
            {
                disposed = true;
                released = [.. subscriptions];
                subscriptions.Clear();
            }

            foreach (var subscription in released)
            {
                subscription.Dispose();
            }
        }

        /// <summary>
        /// Records <paramref name="host"/> as <see cref="BuiltHost"/> and throws a new
        /// <see cref="HostAbortedException"/>, recorded as <see cref="AbortException"/>.
        /// </summary>
        /// <param name="host">The host of the <c>HostBuilt</c> event.</param>
        /// <exception cref="HostAbortedException">Always.</exception>
        public void AbortHostBuilt(IHost host)
        {
            BuiltHost = host;
            AbortException = new HostAbortedException();
            throw AbortException;
        }

        /// <summary>
        /// Keeps <paramref name="subscription"/> for <see cref="Dispose"/>, or disposes it at once when
        /// <see cref="Dispose"/> has already run.
        /// </summary>
        /// <param name="subscription">The subscription to keep.</param>
        private void Track(IDisposable subscription)
        {
            lock (gate)
            {
                if (!disposed)
                {
                    subscriptions.Add(subscription);
                    return;
                }
            }

            subscription.Dispose();
        }
    }

    /// <summary>
    /// Observes the events of one <c>Microsoft.Extensions.Hosting</c> listener. When the listener writes the
    /// <c>HostBuilt</c> event with an <see cref="IHost"/> while <see cref="HostBuiltAbortRequested"/> is set, passes
    /// the host to <see cref="HostBuiltAbortingObserver.AbortHostBuilt"/>, which throws; every other event is ignored.
    /// The subscription to the listener is disposed by <see cref="Dispose"/>, or when the listener ends.
    /// </summary>
    private sealed class HostBuiltEventObserver : IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private const string HostBuiltEventName = "HostBuilt";

        private readonly HostBuiltAbortingObserver owner;

        private readonly IDisposable subscription;

        /// <summary>Subscribes the new observer to <paramref name="listener"/>.</summary>
        /// <param name="listener">The <c>Microsoft.Extensions.Hosting</c> listener to observe.</param>
        /// <param name="owner">The observer that records and throws the abort.</param>
        public HostBuiltEventObserver(DiagnosticListener listener, HostBuiltAbortingObserver owner)
        {
            this.owner = owner;
            subscription = listener.Subscribe(this);
        }

        /// <summary>
        /// Aborts the host of a <c>HostBuilt</c> event while <see cref="HostBuiltAbortRequested"/> is set.
        /// </summary>
        /// <param name="value">The event's name and payload.</param>
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (HostBuiltAbortRequested.Value && value.Key == HostBuiltEventName && value.Value is IHost host)
            {
                owner.AbortHostBuilt(host);
            }
        }

        /// <summary>Disposes the subscription to the listener.</summary>
        public void OnCompleted() => Dispose();

        /// <summary>Disposes the subscription to the listener.</summary>
        /// <param name="error">The error that ended the listener's events.</param>
        public void OnError(Exception error) => Dispose();

        /// <summary>Disposes the subscription to the listener.</summary>
        public void Dispose() => subscription.Dispose();
    }
}
