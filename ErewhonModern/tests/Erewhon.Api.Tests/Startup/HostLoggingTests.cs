using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Erewhon.Api.Tests.Startup;

/// <summary>
/// Characterises the console log of the Erewhon host and the end of a process whose host fails. A host whose
/// configuration has no <c>Serilog:WriteTo</c> entry writes each event once as compact JSON to standard output, with
/// the level overrides of the base <c>appsettings.json</c>, and a host with the committed configuration writes each event
/// once through its configured sink. A failure to build or start the host, for the web server and for a command-line
/// verb, ends <see cref="ErewhonHost.RunAsync"/> with exit code 1 and one fatal <c>ErewhonHostFailed</c> event on
/// standard output instead of an exception. Standard output is captured by replacing <see cref="Console.Out"/> while
/// each case runs.
/// </summary>
public sealed class HostLoggingTests
{
    private const string ProbeCategory = "Erewhon.Api.Tests.Startup.HostLoggingTests";

    private const string HostingDiagnosticsCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

    private const string FatalLevelMarker = "\"@l\":\"Fatal\"";

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
}
