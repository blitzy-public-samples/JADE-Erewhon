using System.Text.RegularExpressions;
using Erewhon.FixtureHost;
using Erewhon.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const int SuccessExitCode = 0;
const int FailureExitCode = 1;
const string Usage = """
    Usage:
      Erewhon.FixtureHost serve --database <name>
          Recreates and migrates the database <name>, starts the Erewhon API with the /__fixtures endpoints on
          http://127.0.0.1 at a free port, writes its base URL as the only line on standard output, and runs until
          standard input closes or the process is asked to stop.
      Erewhon.FixtureHost reset-database <name>
          Drops the database <name> and creates it again empty, without migrations.

    <name> matches ^[a-z_][a-z0-9_]{0,62}$. The server and role come from the environment variable
    ConnectionStrings__ErewhonTests.
    """;

var databaseNamePattern = new Regex(@"^[a-z_][a-z0-9_]{0,62}\z", RegexOptions.CultureInvariant);

TextWriter standardOutput = Console.Out;
Console.SetOut(Console.Error);

string step = $"reading the connection string of the environment variable {PostgresDatabase.EnvironmentVariable}";
try
{
    switch (args)
    {
        case ["serve", "--database", var name] when databaseNamePattern.IsMatch(name):
            await ServeAsync(PostgresDatabase.Named(name), standardOutput, current => step = current);
            return SuccessExitCode;

        case ["reset-database", var name] when databaseNamePattern.IsMatch(name):
            PostgresDatabase resetDatabase = PostgresDatabase.Named(name);
            step = $"recreating database {name}";
            await resetDatabase.RecreateAsync();
            Console.Error.WriteLine($"Database {name} recreated");
            return SuccessExitCode;

        default:
            Console.Error.WriteLine(Usage);
            return FailureExitCode;
    }
}
catch (Exception exception)
{
    string? errorCode = FailureDescription.ErrorCode(exception);
    Console.Error.WriteLine(
        $"Erewhon.FixtureHost failed while {step}: {FailureDescription.TypeName(exception)}"
        + (errorCode is null ? string.Empty : $", error code {errorCode}")
        + ". The server, role and password are taken from the environment variable "
        + $"{PostgresDatabase.EnvironmentVariable}.");
    return FailureExitCode;
}

// Recreates and migrates the database, starts the API with the fixture endpoints and with Serilog writing, whatever the
// working directory, every event at Warning level and above as compact JSON to standard error, except events of the
// Microsoft.EntityFrameworkCore and Npgsql categories below Fatal, writes the base URL without a trailing slash to
// urlOutput, then stops the API when standard input reaches its end or the application begins stopping. A failure to
// read standard input is rethrown after the API has stopped. enterStep receives the description of each step before
// the step runs.
static async Task ServeAsync(PostgresDatabase database, TextWriter urlOutput, Action<string> enterStep)
{
    enterStep($"recreating database {database.DatabaseName}");
    await database.RecreateAsync();
    enterStep($"migrating database {database.DatabaseName}");
    await database.MigrateAsync();

    enterStep("starting the API");
    await using ErewhonApiHost host = await ErewhonApiHost.StartAsync(new ErewhonApiHostOptions
    {
        ConnectionString = database.ConnectionString,
        Settings = new Dictionary<string, string?>
        {
            ["Serilog:Using:0"] = "Serilog.Sinks.Console",
            ["Serilog:MinimumLevel:Default"] = "Warning",
            ["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore"] = "Fatal",
            ["Serilog:MinimumLevel:Override:Npgsql"] = "Fatal",
            ["Serilog:WriteTo:0:Name"] = "Console",
            ["Serilog:WriteTo:0:Args:formatter"] =
                "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact",
            ["Serilog:WriteTo:0:Args:standardErrorFromLevel"] = "Verbose",
        },
        MapEndpoints = FixtureEndpoints.MapFixtureEndpoints,
    });

    enterStep("writing the base URL to standard output");
    await urlOutput.WriteLineAsync(host.BaseAddress.GetLeftPart(UriPartial.Authority));
    await urlOutput.FlushAsync();

    enterStep("waiting for standard input to end or the API to stop");
    var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using CancellationTokenRegistration stoppingRegistration = host.Services
        .GetRequiredService<IHostApplicationLifetime>()
        .ApplicationStopping
        .Register(() => stopping.TrySetResult());

    Task first = await Task.WhenAny(ReadStandardInputToEndAsync(), stopping.Task);

    enterStep("stopping the API");
    await host.StopAsync();
    enterStep("reading standard input");
    await first;
    enterStep("stopping the API");
}

// Reads and discards standard input on a thread-pool thread until the stream reports its end.
static Task ReadStandardInputToEndAsync() => Task.Run(() =>
{
    using Stream input = Console.OpenStandardInput();
    var buffer = new byte[1024];
    int read;
    do
    {
        read = input.Read(buffer, 0, buffer.Length);
    }
    while (read > 0);
});
