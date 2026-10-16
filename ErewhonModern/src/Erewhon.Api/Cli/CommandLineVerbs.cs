using Erewhon.Api.Errors;
using Erewhon.Application.Common;
using Erewhon.Application.Messages;
using Erewhon.Persistence;
using Erewhon.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Erewhon.Api.Cli;

/// <summary>
/// The command-line verbs of the Erewhon host: <c>migrate</c> applies the database migrations, and
/// <c>seed &lt;dataDir&gt;</c> loads the seed data files of a directory through <see cref="InitialDataLoader"/>.
/// Ports the <c>DataLoader</c> application and its initialize method <c>ErewhonModelSchemaApp.dataLoaderApp</c>.
/// </summary>
public static class CommandLineVerbs
{
    /// <summary>The verb that applies the database migrations.</summary>
    public const string MigrateVerb = "migrate";

    /// <summary>The verb that loads the seed data files of the directory named by its one operand.</summary>
    public const string SeedVerb = "seed";

    private const string LoggerCategory = "Erewhon.Api.Cli.CommandLineVerbs";
    private const string VerbScopeProperty = "Verb";
    private const int SeedOperandCount = 1;
    private const int SucceededExitCode = 0;
    private const int FailedExitCode = 1;

    /// <summary>
    /// Returns whether <paramref name="args"/> names a command-line verb: its first element is exactly
    /// <see cref="MigrateVerb"/> or <see cref="SeedVerb"/>, compared case-sensitively.
    /// </summary>
    /// <param name="args">The process arguments, or <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the first argument is <see cref="MigrateVerb"/> or <see cref="SeedVerb"/>;
    /// <see langword="false"/> for <see langword="null"/>, an empty array or any other first argument.
    /// </returns>
    public static bool IsVerb(string[]? args) =>
        args is { Length: > 0 }
        && (string.Equals(args[0], MigrateVerb, StringComparison.Ordinal)
            || string.Equals(args[0], SeedVerb, StringComparison.Ordinal));

    /// <summary>
    /// Runs the <c>migrate</c> or <c>seed &lt;dataDir&gt;</c> command-line verb in one dependency-injection scope of
    /// <paramref name="services"/> and returns the process exit code.
    /// <list type="bullet">
    /// <item><description>
    /// <c>migrate</c> applies the pending migrations to the Erewhon database and writes nothing; further operands are
    /// not read. Returns 0.
    /// </description></item>
    /// <item><description>
    /// <c>seed</c> with other than exactly one operand writes the example command line and returns 1. With an empty
    /// operand it loads nothing, writes nothing and returns 0. Otherwise it loads the directory named by the operand,
    /// trimmed of spaces and made absolute against the current directory, writing the loader's lines to
    /// <paramref name="output"/>, and returns 0.
    /// </description></item>
    /// </list>
    /// A verb that fails with any exception other than a requested cancellation returns 1. The exception is translated
    /// with <see cref="ErrorOutcomeMiddleware.Translate"/>; the lock outcome writes no log event, and any other outcome
    /// writes one event through <see cref="ErrorOutcomeMiddleware.LogOutcome"/>, with the verb as the <c>Verb</c>
    /// property of its log scope. <paramref name="output"/> is flushed before returning.
    /// </summary>
    /// <param name="services">The services of the built, unstarted host.</param>
    /// <param name="args">The process arguments; the first is the verb, the rest are its operands.</param>
    /// <param name="output">The writer that receives the verb's output lines.</param>
    /// <param name="ct">Token that cancels the verb.</param>
    /// <returns>The process exit code: 0 on success, 1 on failure or a wrong operand count.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/>, <paramref name="args"/> or <paramref name="output"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="args"/> does not name a verb (<see cref="IsVerb"/>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled while the verb ran.</exception>
    public static async Task<int> RunAsync(
        IServiceProvider services,
        string[] args,
        TextWriter output,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        if (!IsVerb(args))
        {
            throw new ArgumentException(
                $"The first argument must be '{MigrateVerb}' or '{SeedVerb}'.",
                nameof(args));
        }

        string verb = args[0];
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        try
        {
            return string.Equals(verb, MigrateVerb, StringComparison.Ordinal)
                ? await MigrateAsync(scope.ServiceProvider, ct)
                : await SeedAsync(scope.ServiceProvider, args, output, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            LogFailure(scope.ServiceProvider, verb, failure);
            return FailedExitCode;
        }
        finally
        {
            await output.FlushAsync();
        }
    }

    /// <summary>Applies the pending migrations of <see cref="ErewhonDbContext"/> to its database.</summary>
    /// <param name="scopedServices">The services of the verb's scope.</param>
    /// <param name="ct">Token that cancels the migration.</param>
    /// <returns>0 once the migrations are applied.</returns>
    private static async Task<int> MigrateAsync(IServiceProvider scopedServices, CancellationToken ct)
    {
        ErewhonDbContext db = scopedServices.GetRequiredService<ErewhonDbContext>();
        await db.Database.MigrateAsync(ct);
        return SucceededExitCode;
    }

    /// <summary>
    /// Writes the example command line and returns 1 unless <paramref name="args"/> holds exactly one operand; returns
    /// 0 without loading for an empty operand; otherwise loads the directory named by the operand through
    /// <see cref="InitialDataLoader.LoadAsync"/> and returns 0.
    /// </summary>
    /// <param name="scopedServices">The services of the verb's scope.</param>
    /// <param name="args">The process arguments; the first is the verb.</param>
    /// <param name="output">The writer that receives the example command line or the loader's lines.</param>
    /// <param name="ct">Token that cancels the load.</param>
    /// <returns>0 after a load or for an empty operand; 1 for a wrong operand count.</returns>
    private static async Task<int> SeedAsync(
        IServiceProvider scopedServices,
        string[] args,
        TextWriter output,
        CancellationToken ct)
    {
        if (args.Length - 1 != SeedOperandCount)
        {
            await output.WriteLineAsync(MessageCatalog.Get(MessageKeys.ExampleCommandLine));
            return FailedExitCode;
        }

        string operand = args[1];
        if (JadeValues.IsNull(operand))
        {
            return SucceededExitCode;
        }

        InitialDataLoader loader = scopedServices.GetRequiredService<InitialDataLoader>();
        await loader.LoadAsync(ResolveDataDirectory(operand), output, ct);
        return SucceededExitCode;
    }

    /// <summary>
    /// Returns the operand unchanged when it holds only spaces; otherwise the full path of the operand trimmed of
    /// spaces, resolved against the current directory, or the trimmed operand when it cannot be made a full path.
    /// </summary>
    /// <param name="operand">The non-empty directory operand of <c>seed</c>.</param>
    /// <returns>The directory path passed to the loader.</returns>
    private static string ResolveDataDirectory(string operand)
    {
        string trimmed = JadeValues.TrimBlanks(operand);
        if (trimmed.Length == 0)
        {
            return operand;
        }

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception invalidPath) when (
            invalidPath is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return trimmed;
        }
    }

    /// <summary>
    /// Translates the exception of a failed verb with <see cref="ErrorOutcomeMiddleware.Translate"/>, using the
    /// <see cref="PostgresErrorMapper"/> of the verb's scope when one is registered. Writes nothing when
    /// <see cref="ErrorOutcomeMiddleware.IsLogged"/> is <see langword="false"/> for the translated exception, the lock
    /// outcome; otherwise writes one event through <see cref="ErrorOutcomeMiddleware.LogOutcome"/>, with no session,
    /// inside a log scope whose <c>Verb</c> property is <paramref name="verb"/>.
    /// </summary>
    /// <param name="scopedServices">The services of the verb's scope.</param>
    /// <param name="verb">The verb that failed.</param>
    /// <param name="failure">The exception the verb failed with.</param>
    private static void LogFailure(IServiceProvider scopedServices, string verb, Exception failure)
    {
        PostgresErrorMapper? mapper = scopedServices.GetService<PostgresErrorMapper>();
        Exception translated = ErrorOutcomeMiddleware.Translate(failure, mapper);
        if (!ErrorOutcomeMiddleware.IsLogged(translated))
        {
            return;
        }

        ILogger logger = scopedServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);
        using (logger.BeginScope(new Dictionary<string, object> { [VerbScopeProperty] = verb }))
        {
            ErrorOutcomeMiddleware.LogOutcome(logger, translated, null);
        }
    }
}
