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

try
{
    switch (args)
    {
        case ["serve", "--database", var name] when databaseNamePattern.IsMatch(name):
            await ServeAsync(PostgresDatabase.Named(name), standardOutput);
            return SuccessExitCode;

        case ["reset-database", var name] when databaseNamePattern.IsMatch(name):
            await PostgresDatabase.Named(name).RecreateAsync();
            return SuccessExitCode;

        default:
            Console.Error.WriteLine(Usage);
            return FailureExitCode;
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return FailureExitCode;
}

// Recreates and migrates the database, starts the API with the fixture endpoints, writes the base URL without a
// trailing slash to urlOutput, then stops the API when standard input reaches its end or the application begins
// stopping. A failure to read standard input is rethrown after the API has stopped.
static async Task ServeAsync(PostgresDatabase database, TextWriter urlOutput)
{
    await database.RecreateAsync();
    await database.MigrateAsync();

    await using ErewhonApiHost host = await ErewhonApiHost.StartAsync(new ErewhonApiHostOptions
    {
        ConnectionString = database.ConnectionString,
        MapEndpoints = FixtureEndpoints.MapFixtureEndpoints,
    });

    await urlOutput.WriteLineAsync(host.BaseAddress.GetLeftPart(UriPartial.Authority));
    await urlOutput.FlushAsync();

    var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using CancellationTokenRegistration stoppingRegistration = host.Services
        .GetRequiredService<IHostApplicationLifetime>()
        .ApplicationStopping
        .Register(() => stopping.TrySetResult());

    Task first = await Task.WhenAny(ReadStandardInputToEndAsync(), stopping.Task);

    await host.StopAsync();
    await first;
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
