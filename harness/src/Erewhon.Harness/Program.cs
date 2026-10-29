using System.Runtime.InteropServices;
using Erewhon.Contracts.Catalogue;
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
/// <para><c>replay</c> never fails before its exit path. A configuration, operation catalogue or
/// known-limitations register that cannot be loaded becomes a startup <c>CHK-EXCEPTION</c> item, and
/// <see cref="ReplayMode"/> still writes the report, shuts the facade and the stand-in down, prints the
/// final line and decides the exit code.</para>
/// <para>SIGTERM and SIGINT cancel the running mode instead of ending the process, so a container stop
/// still reaches replay's report and shutdown steps.</para>
/// <para>Program reads only <c>harness/config/harness.json</c> and the register, writes no file and prints
/// only the usage line and fatal errors, to standard error. It configures no credential or header.</para>
/// </remarks>
internal static class Program
{
    /// <summary>The line printed to standard error when no mode or an unknown mode is given.</summary>
    internal const string Usage =
        "usage: Erewhon.Harness <capture|replay|export-openapi|summary> [--Harness:Key=value ...]";

    private const string CaptureModeName = "capture";
    private const string ReplayModeName = "replay";
    private const string ExportOpenApiModeName = "export-openapi";
    private const string SummaryModeName = "summary";

    private const string RepoRootVariable = "Harness__RepoRoot";
    private const string RepoRootKey = HarnessOptions.SectionName + ":" + nameof(HarnessOptions.RepoRoot);

    // HarnessOptions owns the default root, so it is read from a fresh instance rather than restated.
    private static readonly string DefaultRepoRoot = new HarnessOptions().RepoRoot;

    private static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();

        // Declared after the source, so both are disposed before it and stay registered until the mode returns.
        using var terminate = RegisterCancellation(PosixSignal.SIGTERM, cts);
        using var interrupt = RegisterCancellation(PosixSignal.SIGINT, cts);

        return await RunAsync(args, Console.Out, cts.Token).ConfigureAwait(false);
    }

    /// <summary>Runs the mode named by <c>args[0]</c> with the settings that follow it.</summary>
    /// <param name="args">The mode, then optional <c>--Harness:Key=value</c> settings.</param>
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

        var settingArgs = args[1..];
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
    /// Builds the harness settings. Sources, lowest precedence first: <c>harness/config/harness.json</c>
    /// (optional), environment variables without a prefix (<c>Harness__X__Y</c> is <c>Harness:X:Y</c>), then
    /// <paramref name="settingArgs"/>.
    /// </summary>
    /// <remarks>
    /// The repository root is resolved first, from <c>Harness__RepoRoot</c> or <c>--Harness:RepoRoot</c>
    /// (blank means <c>/repo</c>), because <c>harness.json</c> is read from under it; the bound
    /// <see cref="HarnessOptions.RepoRoot"/> is always that root, so the file cannot relocate the tree it was
    /// read from. An empty environment value such as <c>Harness__Backend__AdminUrl=</c> binds to <c>""</c>, so
    /// an external backend needs no post-processing here.
    /// </remarks>
    /// <param name="settingArgs">The arguments after the mode.</param>
    /// <returns>The bound settings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settingArgs"/> is null.</exception>
    /// <exception cref="InvalidDataException"><c>harness.json</c> exists but is not valid JSON.</exception>
    /// <exception cref="InvalidOperationException">A value cannot be converted to its setting's type.</exception>
    internal static HarnessOptions LoadOptions(string[] settingArgs)
    {
        ArgumentNullException.ThrowIfNull(settingArgs);

        var overrides = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddCommandLine(settingArgs)
            .Build();
        var repoRoot = ResolveRepoRoot(overrides[RepoRootKey]);

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(new HarnessOptions { RepoRoot = repoRoot }.HarnessConfigPath, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .AddCommandLine(settingArgs)
            .Build();
        try
        {
            var options = configuration.GetSection(HarnessOptions.SectionName).Get<HarnessOptions>() ?? new HarnessOptions();
            options.RepoRoot = repoRoot;
            return options;
        }
        finally
        {
            // Releases the file provider the JSON source created for the configuration directory.
            (configuration as IDisposable)?.Dispose();
        }
    }

    // Every loading step is guarded on its own, so whatever fails, ReplayMode is constructed and its exit path
    // (report, shutdown, final line, exit code) runs.
    private static async Task<int> RunReplayAsync(string[] settingArgs, TextWriter stdout, CancellationToken ct)
    {
        var startupFailures = new List<FailureItem>();

        HarnessOptions options;
        try
        {
            options = LoadOptions(settingArgs);
        }
        catch (Exception ex)
        {
            options = new HarnessOptions { RepoRoot = ResolveRepoRoot(Environment.GetEnvironmentVariable(RepoRootVariable)) };
            AddStartupFailure(startupFailures, "configuration could not be loaded, so default settings are used", ex);
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
            body = new ReplayBody(options, catalogue, stdout);
        }
        catch (Exception ex)
        {
            body = new FailedReplayBody(ex);
        }

        using var shutdown = new HttpComponentShutdown(options);
        var replay = new ReplayMode(options, body, new ResultsReportWriter(), shutdown, register, stdout, startupFailures);
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

    private static PosixSignalRegistration? RegisterCancellation(PosixSignal signal, CancellationTokenSource cts)
    {
        try
        {
            return PosixSignalRegistration.Create(signal, context =>
            {
                // The process stays alive and the mode ends through cancellation.
                context.Cancel = true;
                try
                {
                    // CancelAsync runs the token's callbacks on the thread pool, so a callback that throws
                    // faults the returned task rather than the signal-handling thread.
                    _ = cts.CancelAsync();
                }
                catch (ObjectDisposedException)
                {
                    // The signal arrived while Main was returning; no mode is left to cancel.
                    return;
                }
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
        // Exception messages and paths can hold line breaks; each error stays on one line.
        var line = message.ReplaceLineEndings(" ");
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
}
