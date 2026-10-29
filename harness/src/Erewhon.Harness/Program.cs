using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Erewhon.Contracts.Catalogue;
using Erewhon.Harness.Baseline;
using Erewhon.Harness.Lifecycle;
using Erewhon.Harness.Modes;
using Erewhon.Harness.Report;
using Microsoft.Extensions.Configuration;

namespace Erewhon.Harness;

/// <summary>
/// The harness entry point:
/// <c>Erewhon.Harness &lt;capture|replay|export-openapi|summary&gt; [--Harness:Key=value ...]</c>. It builds
/// <see cref="HarnessOptions"/> and runs the mode named by the first argument.
/// </summary>
/// <remarks>
/// <para>The exit code is 0 or 1 and nothing else: Compose maps every non-zero exit to 1, and the status of
/// <c>docker compose up --build</c> is the harness's own.</para>
/// <para>Every argument after the mode must be one <c>--&lt;key&gt;=&lt;value&gt;</c> setting whose key names
/// a <see cref="HarnessOptions"/> setting, such as <c>--Harness:Target:AdminUrl=http://facade:9090</c>. Keys
/// match case-insensitively, as configuration keys do, and the value may be empty
/// (<c>--Harness:Backend:AdminUrl=</c> marks an external backend). Any other argument (a bare word,
/// <c>--help</c>, <c>-x</c>, <c>/x</c>, a key without <c>=</c> or an unknown key) gets one line naming its
/// position, and at most its key, then the usage line and exit code 1, before any file, setting or network
/// is read; no mode runs. A value is never echoed, because a URL may carry a password.</para>
/// <para><c>replay</c> never fails before its exit path. An unreadable <c>harness.json</c>, a setting whose
/// value cannot be converted, an execution mode other than <c>docker</c> or <c>local-processes</c>, and an
/// operation catalogue or known-limitations register that cannot be loaded each become a startup
/// <c>CHK-EXCEPTION</c> item. Every other setting keeps its configured value, so <see cref="ReplayMode"/>
/// still writes the report to <c>Harness:ReportPath</c>, shuts down the facade and the stand-in at their
/// configured admin URLs (never an external backend), prints the final line and decides the exit code.</para>
/// <para>SIGTERM and SIGINT cancel the running mode instead of ending the process, so a container stop
/// still reaches replay's report and shutdown steps. The first signal starts the cancellation and later
/// ones are ignored. Once the mode has returned, Main waits up to
/// <see cref="SignalCancellation.CompletionLimit"/> for the token callbacks that cancellation started and
/// disposes the token source only after that wait; a callback that failed, or one still running at the
/// bound, is reported on standard error in one line and makes the exit code 1.</para>
/// <para>Program reads only <c>harness/config/harness.json</c> and the register, writes no file and prints
/// only the usage line and fatal errors, to standard error, each escaped by
/// <see cref="LifecycleConsole.Display"/>. It configures no credential or header.</para>
/// </remarks>
internal static class Program
{
    /// <summary>
    /// The line printed to standard error when no mode or an unknown mode is given, or when an argument after
    /// the mode is not a <c>--Harness:Key=value</c> setting.
    /// </summary>
    internal const string Usage =
        "usage: Erewhon.Harness <capture|replay|export-openapi|summary> [--Harness:Key=value ...]";

    private const string CaptureModeName = "capture";
    private const string ReplayModeName = "replay";
    private const string ExportOpenApiModeName = "export-openapi";
    private const string SummaryModeName = "summary";

    private const string RepoRootKey = HarnessOptions.SectionName + ":" + nameof(HarnessOptions.RepoRoot);
    private const string ExecutionModeKey = HarnessOptions.SectionName + ":" + nameof(HarnessOptions.ExecutionMode);

    // The only argument form after the mode: "--<setting key>=<value>".
    private const string SettingPrefix = "--";
    private const char SettingSeparator = '=';

    // What each kind of settings problem does to the run; the lenient loader records one with each problem.
    private const string ArgumentIgnored = "an argument is not a --Harness:Key=value setting, so it is ignored";
    private const string HarnessJsonUnread = "harness.json could not be read, so its settings are not applied";
    private const string SettingNotConverted = "a setting could not be converted, so it keeps its default";

    // HarnessOptions owns the default root, so it is read from a fresh instance rather than restated.
    private static readonly string DefaultRepoRoot = new HarnessOptions().RepoRoot;

    // The key of every harness setting, derived from HarnessOptions itself, so a new setting needs no change
    // here.
    private static readonly string[] Settings = DiscoverSettings();

    private static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        var cancellation = new SignalCancellation(cts, WriteError);

        int exitCode;

        // Both registrations stay in place until the mode returns and are disposed before the cancellation is
        // completed, so no later signal is dispatched; one already dispatched is ignored once completion begins.
        using (RegisterCancellation(PosixSignal.SIGTERM, cancellation))
        using (RegisterCancellation(PosixSignal.SIGINT, cancellation))
        {
            exitCode = await RunAsync(args, Console.Out, cts.Token).ConfigureAwait(false);
        }

        // The source is disposed only after the callbacks a signal started have finished or the bound has
        // passed; a callback that failed or was still running is reported and makes the exit code 1.
        return await cancellation.CompleteAsync(exitCode, SignalCancellation.CompletionLimit).ConfigureAwait(false);
    }

    /// <summary>Runs the mode named by <c>args[0]</c> with the settings that follow it.</summary>
    /// <param name="args">
    /// The mode, then optional <c>--Harness:Key=value</c> settings, each naming a <see cref="HarnessOptions"/>
    /// setting. An unknown mode, or any other argument after it, prints the problem and the usage line to
    /// standard error and returns 1 before any setting, file or network is read.
    /// </param>
    /// <param name="stdout">Where the mode's console lines go.</param>
    /// <param name="ct">Cancels the mode; replay still runs its exit path.</param>
    /// <returns>0 or 1.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> or <paramref name="stdout"/> is null.</exception>
    internal static async Task<int> RunAsync(string[] args, TextWriter stdout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);

        var mode = args.Length == 0 ? null : args[0];
        if (mode is not (CaptureModeName or ReplayModeName or ExportOpenApiModeName or SummaryModeName))
        {
            WriteError(Usage);
            return 1;
        }

        // Checked before anything is read, so a stray "--help" or an extra word can never start a mode, and
        // a bare key can never take the next argument as its value.
        var settingArgs = args[1..];
        for (var i = 0; i < settingArgs.Length; i++)
        {
            if (DescribeArgumentProblem(settingArgs[i]) is { } problem)
            {
                WriteError(string.Create(CultureInfo.InvariantCulture, $"{mode}: argument {i + 2}{problem}"));
                WriteError(Usage);
                return 1;
            }
        }

        try
        {
            var result = mode == ReplayModeName
                ? await RunReplayAsync(settingArgs, stdout, ct).ConfigureAwait(false)
                : await RunModeAsync(mode, settingArgs, stdout, ct).ConfigureAwait(false);
            return result == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            WriteError($"{mode}: failed: {ReplayMode.Describe(ex)}");
            return 1;
        }
    }

    /// <summary>
    /// Builds the harness settings and throws the first problem it meets. Sources, lowest precedence first:
    /// <c>harness/config/harness.json</c> (optional), environment variables without a prefix
    /// (<c>Harness__X__Y</c> is <c>Harness:X:Y</c>), then <paramref name="settingArgs"/>.
    /// </summary>
    /// <remarks>
    /// <para>The repository root is resolved first, from <c>Harness__RepoRoot</c> or <c>--Harness:RepoRoot</c>
    /// (blank means <c>/repo</c>), because <c>harness.json</c> is read from under it; the bound
    /// <see cref="HarnessOptions.RepoRoot"/> is always that root, so the file cannot relocate the tree it was
    /// read from. An empty value such as <c>Harness__Backend__AdminUrl=</c> binds to <c>""</c>, so an external
    /// backend needs no post-processing here.</para>
    /// <para>The three sources form one configuration, read once, and each setting is bound from it on its
    /// own with the same result as binding the whole section: a value is converted as the binder converts
    /// it, an explicit <c>null</c> in <c>harness.json</c> binds as the binder binds <c>null</c> (a null
    /// backend admin URL marks an external backend), and a key that no source sets keeps its
    /// <see cref="HarnessOptions"/> default. Computed members such as
    /// <see cref="HarnessOptions.EffectiveReportPath"/> are not settings and are never bound.</para>
    /// </remarks>
    /// <param name="settingArgs">The arguments after the mode, each a <c>--Harness:Key=value</c> setting.</param>
    /// <returns>The bound settings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settingArgs"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// An argument is not a <c>--Harness:Key=value</c> setting, or the repository root cannot form a path.
    /// </exception>
    /// <exception cref="InvalidDataException"><c>harness.json</c> exists but is not valid JSON.</exception>
    /// <exception cref="InvalidOperationException">A value cannot be converted to its setting's type; the message names the key.</exception>
    internal static HarnessOptions LoadOptions(string[] settingArgs) => Load(settingArgs, problems: null);

    /// <summary>
    /// Builds the harness settings as <see cref="LoadOptions(string[])"/> does, but records every problem in
    /// <paramref name="problems"/> instead of throwing it, so that one bad item never discards the others.
    /// </summary>
    /// <remarks>
    /// <para>An argument that is not a <c>--Harness:Key=value</c> setting is left out. When
    /// <c>harness.json</c> cannot be read (malformed, unreadable, or under a root that cannot form a path),
    /// the environment and the arguments still apply. A setting whose value cannot be converted keeps its
    /// <see cref="HarnessOptions"/> default, and every other setting keeps its value: the repository root,
    /// the report path and the admin URLs that decide where the report goes and what cleanup shuts down,
    /// an empty backend admin URL included.</para>
    /// <para>Each problem carries the exception the failing step raised, the same one
    /// <see cref="LoadOptions(string[])"/> would throw, and a context stating its effect.</para>
    /// </remarks>
    /// <param name="settingArgs">The arguments after the mode.</param>
    /// <param name="problems">Receives each problem, in the order met.</param>
    /// <returns>The settings bound from everything that could be read and converted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settingArgs"/> or <paramref name="problems"/> is null.</exception>
    internal static HarnessOptions LoadOptions(string[] settingArgs, ICollection<OptionsProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return Load(settingArgs, problems);
    }

    // The loader behind both overloads: a null problems collection throws each problem where it is met, and a
    // collection records it and carries on with what remains.
    private static HarnessOptions Load(string[] settingArgs, ICollection<OptionsProblem>? problems)
    {
        ArgumentNullException.ThrowIfNull(settingArgs);

        var accepted = new List<string>(settingArgs.Length);
        for (var i = 0; i < settingArgs.Length; i++)
        {
            if (DescribeArgumentProblem(settingArgs[i]) is { } argumentProblem)
            {
                var ex = new ArgumentException($"setting argument {i + 1}{argumentProblem}", nameof(settingArgs));
                if (problems is null)
                {
                    throw ex;
                }

                problems.Add(new OptionsProblem(ArgumentIgnored, ex));
                continue;
            }

            accepted.Add(settingArgs[i]);
        }

        // The environment and the arguments alone, which locate harness.json and stand in for the full
        // configuration when that file cannot be read.
        var overrides = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddCommandLine([.. accepted])
            .Build();
        IConfigurationRoot? merged = null;
        try
        {
            var repoRoot = ResolveRepoRoot(overrides[RepoRootKey]);

            // All three sources in one configuration, read once: the arguments outrank the environment, which
            // outranks harness.json, and a value of "" from any of them is a value. Joining separate
            // configurations instead would read "" as unset and turn an empty backend admin URL back into
            // the stand-in's. A file that cannot be read loses only its own settings.
            try
            {
                merged = new ConfigurationBuilder()
                    .AddJsonFile(new HarnessOptions { RepoRoot = repoRoot }.HarnessConfigPath, optional: true, reloadOnChange: false)
                    .AddEnvironmentVariables()
                    .AddCommandLine([.. accepted])
                    .Build();
            }
            catch (Exception ex) when (problems is not null)
            {
                problems.Add(new OptionsProblem(HarnessJsonUnread, ex));
            }

            var configuration = merged ?? overrides;
            var options = new HarnessOptions();
            foreach (var key in Settings)
            {
                try
                {
                    BindSetting(configuration, key, options);
                }
                catch (Exception ex) when (problems is not null)
                {
                    problems.Add(new OptionsProblem(SettingNotConverted, ex));
                }
            }

            options.RepoRoot = repoRoot;
            return options;
        }
        finally
        {
            // Releases the file provider the JSON source created for the configuration directory.
            (merged as IDisposable)?.Dispose();
            (overrides as IDisposable)?.Dispose();
        }
    }

    // Binds the one setting at key into options exactly as binding the whole section would, and leaves every
    // other setting as it is. The binder sees only the entries the sources hold for that key: the key itself
    // when a source sets it, an explicit JSON null included, and anything nested under it. So null binds as
    // null (a null backend admin URL marks an external backend), "" binds as "", and a key no source sets
    // keeps its default. A value the binder cannot convert throws its InvalidOperationException, which names
    // the key.
    private static void BindSetting(IConfigurationRoot configuration, string key, HarnessOptions options)
    {
        // A section path that only leads to a deeper key is not an entry of its own, so it is left out.
        var entries = configuration.GetSection(key).AsEnumerable()
            .Where(entry => configuration.Providers.Any(provider => provider.TryGet(entry.Key, out _)))
            .ToList();
        if (entries.Count == 0)
        {
            return;
        }

        var setting = new ConfigurationBuilder().AddInMemoryCollection(entries).Build();
        try
        {
            setting.GetSection(HarnessOptions.SectionName).Bind(options);
        }
        finally
        {
            (setting as IDisposable)?.Dispose();
        }
    }

    // Null when the argument is a --<setting key>=<value> setting; otherwise the problem, worded to follow
    // "argument N". Only the key part before '=' is ever named: the value, or a whole argument, may be a URL
    // that carries a password.
    private static string? DescribeArgumentProblem(string? argument)
    {
        var separator = argument?.IndexOf(SettingSeparator) ?? -1;
        if (argument is null || !argument.StartsWith(SettingPrefix, StringComparison.Ordinal) || separator <= SettingPrefix.Length)
        {
            return " is not a --Harness:Key=value setting";
        }

        var key = argument[SettingPrefix.Length..separator];
        return Array.Exists(Settings, setting => string.Equals(setting, key, StringComparison.OrdinalIgnoreCase))
            ? null
            : $": '{key}' is not a harness setting";
    }

    // The configuration key of every setting: each public instance property of HarnessOptions with a public
    // setter is one setting, and each HarnessEndpoints property contributes one setting per settable property
    // of its own, for example Harness:Target:AdminUrl.
    private static string[] DiscoverSettings()
    {
        var settings = new List<string>();
        foreach (var property in SettableProperties(typeof(HarnessOptions)))
        {
            var key = ConfigurationPath.Combine(HarnessOptions.SectionName, property.Name);
            if (property.PropertyType == typeof(HarnessEndpoints))
            {
                settings.AddRange(SettableProperties(typeof(HarnessEndpoints))
                    .Select(leaf => ConfigurationPath.Combine(key, leaf.Name)));
            }
            else
            {
                settings.Add(key);
            }
        }

        return [.. settings];
    }

    private static IEnumerable<PropertyInfo> SettableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod is { IsPublic: true } && property.GetIndexParameters().Length == 0);

    // Every loading step is guarded on its own, so whatever fails, ReplayMode is constructed and its exit path
    // (report, shutdown, final line, exit code) runs. The settings loader records its problems rather than
    // throwing them, so a bad item never replaces the report path or the admin URLs cleanup uses.
    private static async Task<int> RunReplayAsync(string[] settingArgs, TextWriter stdout, CancellationToken ct)
    {
        var startupFailures = new List<FailureItem>();

        var problems = new List<OptionsProblem>();
        var options = LoadOptions(settingArgs, problems);
        foreach (var problem in problems)
        {
            AddStartupFailure(startupFailures, problem.Context, problem.Exception);
        }

        // The report states how the run was executed, so a value that names neither way is a configuration
        // error to report, never a fact to repeat; the run itself goes ahead.
        if (options.RecognisedExecutionMode is null)
        {
            AddStartupFailure(
                startupFailures,
                "the execution mode is not recognised, so the report states it as unknown",
                new InvalidOperationException(
                    $"{ExecutionModeKey} '{options.ExecutionMode?.Trim()}' is neither '{CaptureExecutionValues.LocalProcesses}' nor '{CaptureExecutionValues.Docker}'"));
        }

        OperationCatalogue? catalogue = null;
        try
        {
            catalogue = OperationCatalogue.Default;
        }
        catch (Exception ex)
        {
            AddStartupFailure(startupFailures, "operation catalogue could not be loaded", ex);
        }

        KnownLimitationsRegister register;
        try
        {
            register = KnownLimitationsRegister.Load(options.KnownLimitationsPath);
        }
        catch (Exception ex)
        {
            register = KnownLimitationsRegister.Empty;
            AddStartupFailure(startupFailures, "known-limitations register could not be loaded, so no failing item is documented", ex);
        }

        IReplayBody body;
        try
        {
            body = new ReplayBody(options, catalogue, register, stdout);
        }
        catch (Exception ex)
        {
            body = new FailedReplayBody(ex);
        }

        ResultsReportWriter writer;
        try
        {
            writer = new ResultsReportWriter(options.ResolvePath("."));
        }
        catch (Exception ex)
        {
            // A writer without a root still renders the report, which the failed write then prints.
            writer = new ResultsReportWriter();
            AddStartupFailure(startupFailures, "the repository root is unusable, so the report cannot be written", ex);
        }

        using var shutdown = new HttpComponentShutdown(options);
        var replay = new ReplayMode(options, body, writer, shutdown, register, stdout, startupFailures);
        return await replay.RunAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> RunModeAsync(string mode, string[] settingArgs, TextWriter stdout, CancellationToken ct)
    {
        var options = LoadOptions(settingArgs);
        return mode switch
        {
            CaptureModeName => await new CaptureMode(options, OperationCatalogue.Default, stdout).RunAsync(ct).ConfigureAwait(false),
            ExportOpenApiModeName => await new ExportOpenApiMode(options, stdout).RunAsync(ct).ConfigureAwait(false),
            SummaryModeName => await new SummaryMode(options, stdout).RunAsync(ct).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a harness mode."),
        };
    }

    private static string ResolveRepoRoot(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultRepoRoot : configured;

    private static void AddStartupFailure(List<FailureItem> failures, string context, Exception ex)
    {
        failures.Add(new FailureItem(
            FailureItemIds.Exception,
            FailureItemIds.ExceptionNature(ex),
            Detail: $"{context}: {ReplayMode.SafeMessage(ex)}"));
        WriteError($"{ReplayModeName}: startup: {context}: {ReplayMode.Describe(ex)}");
    }

    private static PosixSignalRegistration? RegisterCancellation(PosixSignal signal, SignalCancellation cancellation)
    {
        try
        {
            return PosixSignalRegistration.Create(signal, context =>
            {
                // The process stays alive and the mode ends through cancellation.
                context.Cancel = true;

                // Never waits: the token's callbacks run on the thread pool, and Main observes their task.
                cancellation.Request();
            });
        }
        catch (PlatformNotSupportedException)
        {
            // The signal keeps its default handling where the platform cannot register a handler for it.
            return null;
        }
    }

    private static void WriteError(string message)
    {
        // Exception messages, paths and settings can hold line breaks, terminal escapes and bidirectional
        // controls; Display escapes each of them, so every error stays one inert line.
        var line = LifecycleConsole.Display(message);
        try
        {
            Console.Error.WriteLine(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Standard error is the only channel for this line; the exit code still reports the failure.
            return;
        }
    }

    /// <summary>
    /// Stands in for a <see cref="ReplayBody"/> whose construction threw: it rethrows that exception when run,
    /// so <see cref="ReplayMode"/> records it as <c>CHK-EXCEPTION</c> and still runs its exit path.
    /// </summary>
    private sealed class FailedReplayBody(Exception failure) : IReplayBody
    {
        public Task ExecuteAsync(ReplayRunState state, CancellationToken ct) => Task.FromException(failure);
    }

    /// <summary>
    /// A settings problem that <see cref="LoadOptions(string[], ICollection{OptionsProblem})"/> recorded
    /// instead of throwing.
    /// </summary>
    /// <param name="Context">What the problem did to the settings, for example that a setting keeps its default.</param>
    /// <param name="Exception">The exception the failing step raised.</param>
    internal sealed record OptionsProblem(string Context, Exception Exception);

    /// <summary>
    /// Turns SIGTERM and SIGINT into one cancellation of the running mode and keeps the task that runs the
    /// token's callbacks, so their completion, or their failure, is observed before the source is disposed.
    /// </summary>
    /// <remarks>
    /// <see cref="Request"/> is the signal handler's only call: it starts the cancellation once and never
    /// waits for a callback. <see cref="CompleteAsync"/> ends request handling first, so a handler that was
    /// dispatched before its registration was disposed but runs afterwards starts nothing, and then waits for
    /// the retained task within a bound.
    /// </remarks>
    /// <param name="source">The source whose token the running mode observes.</param>
    /// <param name="writeError">Writes one line to standard error.</param>
    internal sealed class SignalCancellation(CancellationTokenSource source, Action<string> writeError)
    {
        /// <summary>
        /// How long Main waits, once the mode has returned, for the callbacks a signal started. They only
        /// cancel in-flight requests and delays, so they finish far inside it; the bound keeps a callback that
        /// hangs from holding the process open after the mode has finished.
        /// </summary>
        internal static readonly TimeSpan CompletionLimit = TimeSpan.FromSeconds(5);

        // Serialises requests with each other and with completion, so the first request's task is the one
        // kept and no request starts once completion has read it. CancelAsync only moves the source to the
        // cancelled state and queues the callbacks, so the lock is never held while a callback runs.
        private readonly Lock _gate = new();
        private Task? _callbacks;
        private bool _completing;

        /// <summary>The task running the token's callbacks for the first request; null before any request.</summary>
        internal Task? Callbacks
        {
            get
            {
                lock (_gate)
                {
                    return _callbacks;
                }
            }
        }

        /// <summary>
        /// Requests cancellation of the source without waiting for its callbacks. Only the first request
        /// starts it; a later request, one made once completion has begun and one made after the source was
        /// disposed do nothing.
        /// </summary>
        internal void Request()
        {
            lock (_gate)
            {
                if (_completing || _callbacks is not null)
                {
                    return;
                }

                Task callbacks;
                try
                {
                    callbacks = source.CancelAsync();
                }
                catch (ObjectDisposedException)
                {
                    // The source is gone, so no mode is left to cancel.
                    return;
                }

                // A disposed source can also be reported through a task that has already faulted with
                // ObjectDisposedException; reading it observes the fault, and nothing is left to cancel. A
                // callback's own failure is wrapped in AggregateException, so it is never mistaken for this.
                if (callbacks is { IsFaulted: true, Exception.InnerException: ObjectDisposedException })
                {
                    return;
                }

                _callbacks = callbacks;
            }
        }

        /// <summary>
        /// Ends request handling, then waits up to <paramref name="bound"/> for the callbacks the first
        /// request started. Never throws for a callback's failure or for a callback still running.
        /// </summary>
        /// <param name="exitCode">The mode's exit code.</param>
        /// <param name="bound">The longest wait for the callbacks.</param>
        /// <returns>
        /// <paramref name="exitCode"/> when no request started a cancellation or its callbacks completed;
        /// otherwise 1, after one line on standard error that describes the callbacks' failure or says they
        /// were still running at the bound.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="bound"/> is negative, infinite included, or longer than a task wait can be timed.
        /// </exception>
        internal async Task<int> CompleteAsync(int exitCode, TimeSpan bound)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(bound, TimeSpan.Zero);

            Task? callbacks;
            lock (_gate)
            {
                _completing = true;
                callbacks = _callbacks;
            }

            if (callbacks is null)
            {
                return exitCode;
            }

            // Created outside the guard, so a bound too long to time is reported as such and never as callbacks
            // still running.
            var wait = callbacks.WaitAsync(bound);
            Exception? waitFailure = null;
            try
            {
                await wait.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Classified by the task's state below, because a callback can itself throw TimeoutException.
                waitFailure = ex;
            }

            if (callbacks.IsCompletedSuccessfully)
            {
                return exitCode;
            }

            writeError(callbacks.IsCompleted
                ? $"cancellation callbacks failed: {DescribeFailure(callbacks, waitFailure)}"
                : string.Create(CultureInfo.InvariantCulture, $"cancellation callbacks were still running after {bound.TotalSeconds} s"));
            return 1;
        }

        // CancelAsync faults its task with one AggregateException that holds every callback's exception;
        // reading Exception also marks the fault as observed.
        private static string DescribeFailure(Task callbacks, Exception? waitFailure)
        {
            if (callbacks.Exception is { } fault)
            {
                return string.Join("; ", fault.Flatten().InnerExceptions.Select(ReplayMode.Describe));
            }

            // A cancelled task carries no exception of its own; awaiting it threw TaskCanceledException.
            return waitFailure is null ? "the callbacks were cancelled" : ReplayMode.Describe(waitFailure);
        }
    }
}
