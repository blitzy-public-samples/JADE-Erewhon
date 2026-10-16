using System.Diagnostics;
using System.Reflection;
using Erewhon.Api.Cli;
using Erewhon.Application.Messages;
using Erewhon.Testing;
using Npgsql;
using Xunit;

namespace Erewhon.Seed.Tests;

/// <summary>
/// The <c>seed</c> verb started through <c>dotnet run</c>: each case recreates and migrates the assembly database
/// <c>erewhon_test_erewhon_seed_tests</c>, runs <c>dotnet run --project &lt;project&gt; -c &lt;configuration&gt;
/// --no-build -- seed &lt;data dir&gt;</c> from a folder of <c>ErewhonModern</c> with a relative
/// <c>&lt;data dir&gt;</c>, <c>ConnectionStrings__Erewhon</c> set to that database and no content-root variable, and
/// checks the exit code, the first loader line and the table counts. The configuration is the one the
/// <c>Erewhon.Api</c> assembly of this test run was built with.
/// </summary>
public sealed class SeedCommandLineTests : IAsyncLifetime
{
    private const string DotNetExecutable = "dotnet";
    private const string ConnectionStringVariable = "ConnectionStrings__Erewhon";
    private const string LocationsFile = "Locations.txt";
    private static readonly string[] ContentRootVariables = ["ASPNETCORE_CONTENTROOT", "DOTNET_CONTENTROOT"];
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    private readonly PostgresDatabase _database = PostgresDatabase.ForAssembly(typeof(SeedCommandLineTests).Assembly);

    /// <summary>Drops, recreates and migrates the test database.</summary>
    /// <returns>A task that completes when the database is migrated.</returns>
    public async Task InitializeAsync()
    {
        await _database.RecreateAsync();
        await _database.MigrateAsync();
    }

    /// <summary>Closes every pooled connection of the process.</summary>
    /// <returns>A completed task.</returns>
    public Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        return Task.CompletedTask;
    }

    /// <summary>
    /// <c>dotnet run --project &lt;project&gt; -- seed &lt;data dir&gt;</c> resolves the relative data directory from
    /// the folder the command is run from: from <c>ErewhonModern</c>, the documented
    /// <c>dotnet run --project src/Erewhon.Api -- seed ../Erewhon/DataFiles</c>; from <c>ErewhonModern/tests</c>,
    /// <c>--project ../src/Erewhon.Api</c> with <c>../../Erewhon/DataFiles</c>. Each exits with 0, writes
    /// <c>Loading &lt;Erewhon/DataFiles&gt;/Locations.txt...</c>, and leaves 1 company, 8 agents, 20 clients, 20 sales
    /// and 100 tenders.
    /// </summary>
    /// <param name="workingDirectory">The folder the command runs from, relative to <c>ErewhonModern</c>.</param>
    /// <param name="project">The <c>--project</c> value, relative to <paramref name="workingDirectory"/>.</param>
    /// <param name="dataDirectory">The <c>seed</c> operand, relative to <paramref name="workingDirectory"/>.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [InlineData("", "src/Erewhon.Api", "../Erewhon/DataFiles")]
    [InlineData("tests", "../src/Erewhon.Api", "../../Erewhon/DataFiles")]
    public async Task RelativeOperandResolvesFromInvokingDirectory(
        string workingDirectory,
        string project,
        string dataDirectory)
    {
        string configuration = typeof(CommandLineVerbs).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
            ?? throw new InvalidOperationException("The Erewhon.Api assembly carries no build configuration.");

        var startInfo = new ProcessStartInfo(DotNetExecutable)
        {
            WorkingDirectory = Path.Combine(ErewhonApiHost.FindErewhonModernRoot(), workingDirectory),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[]
        {
            "run", "--project", project, "-c", configuration, "--no-build", "--", CommandLineVerbs.SeedVerb, dataDirectory,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment[ConnectionStringVariable] = _database.ConnectionString;
        foreach (string variable in ContentRootVariables)
        {
            startInfo.Environment.Remove(variable);
        }

        (int exitCode, string standardOutput, string standardError) = await RunAsync(startInfo);

        Assert.True(
            exitCode == 0,
            $"Exit code {exitCode}.{Environment.NewLine}{standardOutput}{Environment.NewLine}{standardError}");
        string firstLoaderLine = MessageCatalog.Get(
            MessageKeys.Loading,
            Path.Combine(ExpectedSeedModel.FindDataDirectory(), LocationsFile));
        Assert.Contains(firstLoaderLine, standardOutput.ReplaceLineEndings("\n").Split('\n'));
        Assert.Equal((1L, 8L, 20L, 20L, 100L), await CountAsync());
    }

    /// <summary>
    /// Starts <paramref name="startInfo"/>, reads its standard output and standard error to their ends and waits for
    /// it to exit; kills the process and its descendants and throws when it runs longer than five minutes.
    /// </summary>
    /// <param name="startInfo">The process to run, with both output streams redirected.</param>
    /// <returns>The exit code, the standard output and the standard error.</returns>
    /// <exception cref="InvalidOperationException">The process could not be started.</exception>
    /// <exception cref="TimeoutException">The process ran longer than five minutes.</exception>
    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(
        ProcessStartInfo startInfo)
    {
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"'{startInfo.FileName}' could not be started.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException(
                $"'{startInfo.FileName} {string.Join(' ', startInfo.ArgumentList)}' ran longer than {CommandTimeout}.");
        }

        return (process.ExitCode, await standardOutput, await standardError);
    }

    /// <summary>Counts the companies, agents, clients, sales and tenders of the test database.</summary>
    /// <returns>The five counts, in that order.</returns>
    private async Task<(long Companies, long Agents, long Clients, long Sales, long Tenders)> CountAsync()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT (SELECT count(*) FROM companies), (SELECT count(*) FROM agents), (SELECT count(*) FROM clients), "
            + "(SELECT count(*) FROM sales), (SELECT count(*) FROM tenders)",
            connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("The count query returned no row.");
        }

        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
    }
}
