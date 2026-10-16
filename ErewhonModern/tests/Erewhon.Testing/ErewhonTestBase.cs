using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Erewhon.Api.Sessions;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Erewhon.Testing;

/// <summary>
/// The <see cref="ISessionContext"/> of a test host's service scopes that serve no HTTP request, such as a test's own
/// scope: one session with an id of its own and the agent and client set through <see cref="SetAgent"/> and
/// <see cref="SetClient"/>, both <see langword="null"/> at first. The session is not held in
/// <see cref="SessionRegistry"/>.
/// </summary>
public sealed class TestSessionContext : ISessionContext
{
    /// <summary>Creates a context whose <see cref="SessionId"/> is a new value of 32 lower-case hexadecimal characters.</summary>
    public TestSessionContext()
    {
        SessionId = Guid.NewGuid().ToString("N");
    }

    /// <summary>Always <see langword="true"/>.</summary>
    public bool HasSession => true;

    /// <summary>The id assigned on construction: 32 lower-case hexadecimal characters, never <see langword="null"/>.</summary>
    public string? SessionId { get; }

    /// <summary>
    /// <see cref="SessionRole.Client"/> when <see cref="ClientId"/> is set, otherwise <see cref="SessionRole.Agent"/> when
    /// <see cref="AgentId"/> is set, otherwise <see cref="SessionRole.Administrator"/>.
    /// </summary>
    public SessionRole Role => ClientId is not null
        ? SessionRole.Client
        : AgentId is not null ? SessionRole.Agent : SessionRole.Administrator;

    /// <inheritdoc />
    public long? AgentId { get; private set; }

    /// <inheritdoc />
    public long? ClientId { get; private set; }

    /// <inheritdoc />
    public bool IsCompanyUser => AgentId is null && ClientId is null;

    /// <summary>
    /// Registers <see cref="TestSessionContext"/> as a scoped service and replaces every <see cref="ISessionContext"/>
    /// registration with a scoped one that resolves, in a scope whose <see cref="IHttpContextAccessor.HttpContext"/> is
    /// set, a new <see cref="HttpSessionContext"/>, and in any other scope the scope's <see cref="TestSessionContext"/>.
    /// </summary>
    /// <remarks>
    /// The collection must also register <see cref="IHttpContextAccessor"/> and the singleton
    /// <see cref="SessionRegistry"/>, as <c>ErewhonHost.Build</c> does.
    /// </remarks>
    /// <param name="services">The service collection of a host built by <c>ErewhonHost.Build</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<TestSessionContext>();
        services.RemoveAll<ISessionContext>();
        services.AddScoped<ISessionContext>(provider =>
            provider.GetRequiredService<IHttpContextAccessor>().HttpContext is null
                ? provider.GetRequiredService<TestSessionContext>()
                : ActivatorUtilities.CreateInstance<HttpSessionContext>(provider));
    }

    /// <summary>
    /// Sets <see cref="AgentId"/> to <paramref name="agentId"/>, or clears it when <paramref name="agentId"/> is
    /// <see langword="null"/>; <see cref="ClientId"/> is unchanged.
    /// </summary>
    /// <param name="agentId">The internal id (<c>agents.id</c>) of the agent, or <see langword="null"/>.</param>
    public void SetAgent(long? agentId) => AgentId = agentId;

    /// <summary>
    /// Sets <see cref="ClientId"/> to <paramref name="clientId"/>, or clears it when <paramref name="clientId"/> is
    /// <see langword="null"/>; <see cref="AgentId"/> is unchanged.
    /// </summary>
    /// <param name="clientId">The internal id (<c>clients.id</c>) of the client, or <see langword="null"/>.</param>
    public void SetClient(long? clientId) => ClientId = clientId;
}

/// <summary>
/// The base class of a .NET test class whose tests run against the real Erewhon API on Kestrel and the PostgreSQL test
/// database of the test assembly. Each test starts with one fresh company, built through
/// <see cref="TestCompanyBuilder"/> and the production application services, and ends with no company. Ports the
/// per-test lifecycle of the JADE test classes: <c>setUp</c>/<c>beforeTest</c> before each test and
/// <c>tearDown</c>/<c>afterTest</c> after it.
/// </summary>
/// <remarks>
/// <para>
/// The first test of an assembly to initialise recreates and migrates the assembly database
/// (<see cref="PostgresDatabase.ForAssembly"/>) and starts one <see cref="ErewhonApiHost"/> over it with the default
/// options, a <see cref="CollectingLogSink"/> and <see cref="TestSessionContext.Register"/>. Every later test of the
/// assembly in the same process uses that host, which is stopped and disposed when the process exits, waiting at most
/// five seconds. A failed preparation or start fails every test of the assembly with the same exception.
/// </para>
/// <para>
/// <see cref="InitializeAsync"/> clears <see cref="LogSink"/>, opens the test's service scope on <see cref="Api"/>,
/// takes the scope's <see cref="TestSessionContext"/> as <see cref="Session"/>, creates <see cref="Builder"/> over the
/// scope and runs <see cref="SetUpAsync"/>. When a step after the scope is opened fails, it deletes every company,
/// removes the session's cart, disposes the scope and rethrows. <see cref="DisposeAsync"/> runs
/// <see cref="TearDownAsync"/>, removes the session's cart and disposes the scope.
/// </para>
/// <para>
/// The tests of one assembly must not run in parallel.
/// </para>
/// </remarks>
public abstract class ErewhonTestBase : IAsyncLifetime
{
    private static readonly TimeSpan ProcessExitStopTimeout = TimeSpan.FromSeconds(5);

    private static readonly ConcurrentDictionary<Assembly, Lazy<Task<SharedHost>>> Hosts = new();

    private SharedHost? _shared;
    private AsyncServiceScope? _scope;
    private TestSessionContext? _session;
    private TestCompanyBuilder? _builder;

    /// <summary>The Erewhon API shared by every test of the test assembly, running over <see cref="Database"/>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="InitializeAsync"/>.</exception>
    protected ErewhonApiHost Api => _shared?.Host ?? throw NotInitialized(nameof(Api));

    /// <summary>The PostgreSQL test database of the test assembly.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="InitializeAsync"/>.</exception>
    protected PostgresDatabase Database => _shared?.Database ?? throw NotInitialized(nameof(Database));

    /// <summary>The fixture builder over <see cref="Services"/>; its <c>My*</c> properties hold this test's fixtures.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="InitializeAsync"/>.</exception>
    protected TestCompanyBuilder Builder => _builder ?? throw NotInitialized(nameof(Builder));

    /// <summary>
    /// The <see cref="TestSessionContext"/> of <see cref="Services"/>, the instance its <see cref="ISessionContext"/>
    /// resolves to: the <c>app</c> of <c>app.setAgent</c>, <c>app.setClient</c> and <c>app.isCompanyUser</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="InitializeAsync"/>.</exception>
    protected TestSessionContext Session => _session ?? throw NotInitialized(nameof(Session));

    /// <summary>
    /// The service provider of this test's scope on <see cref="Api"/>: one database context, unit of work, bound
    /// company and session context for the whole test.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Read before <see cref="InitializeAsync"/> or after <see cref="DisposeAsync"/> disposed the scope.
    /// </exception>
    protected IServiceProvider Services =>
        (_scope ?? throw new InvalidOperationException(
            $"{nameof(Services)} is available from {nameof(InitializeAsync)} until {nameof(DisposeAsync)} disposes the test scope."))
        .ServiceProvider;

    /// <summary>
    /// The log sink of <see cref="Api"/>, holding the events written since this test's <see cref="InitializeAsync"/>
    /// began.
    /// </summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="InitializeAsync"/>.</exception>
    protected CollectingLogSink LogSink => _shared?.LogSink ?? throw NotInitialized(nameof(LogSink));

    /// <summary>Returns the service of type <typeparamref name="T"/> from <see cref="Services"/>.</summary>
    /// <typeparam name="T">The type of the service.</typeparam>
    /// <returns>The service.</returns>
    /// <exception cref="InvalidOperationException">
    /// No service of type <typeparamref name="T"/> is registered, or <see cref="Services"/> is not available.
    /// </exception>
    protected T GetService<T>()
        where T : notnull =>
        Services.GetRequiredService<T>();

    /// <summary>
    /// Prepares the test: obtains the shared host of the test assembly, clears its log sink, opens the test's scope,
    /// takes the scope's <see cref="TestSessionContext"/> as <see cref="Session"/>, creates <see cref="Builder"/> over
    /// the scope and runs <see cref="SetUpAsync"/>. When a step after the scope is opened fails, deletes every company in
    /// a new scope, removes the session's cart, disposes the test's scope and rethrows the failure.
    /// </summary>
    /// <returns>A task that completes when the test's fixtures exist.</returns>
    /// <exception cref="InvalidOperationException">
    /// <c>ConnectionStrings__ErewhonTests</c> is unset or invalid, or a fixture was not persisted.
    /// </exception>
    /// <exception cref="AggregateException">
    /// The set-up failed and its clean-up failed too; holds the set-up failure first, then each clean-up failure.
    /// </exception>
    public async Task InitializeAsync()
    {
        SharedHost shared = await GetSharedHostAsync(GetType().Assembly);
        shared.LogSink.Clear();
        _shared = shared;

        _scope = Api.Services.CreateAsyncScope();
        try
        {
            _session = Services.GetRequiredService<TestSessionContext>();
            _builder = new TestCompanyBuilder(Services);

            await SetUpAsync();
        }
        catch (Exception setUpFailure)
        {
            var failures = new List<Exception> { setUpFailure };
            try
            {
                await DeleteCompaniesAsync();
            }
            catch (Exception deleteFailure)
            {
                failures.Add(deleteFailure);
            }

            await ReleaseScopeAsync(failures);
            ThrowFailures(failures, "The test set-up failed, and its clean-up failed too.");
        }
    }

    /// <summary>
    /// Ends the test: runs <see cref="TearDownAsync"/>, then removes the session's cart from <see cref="ICartStore"/>,
    /// then disposes the test's scope. The cart is removed and the scope disposed even when an earlier step fails. Does
    /// nothing when no test scope is open.
    /// </summary>
    /// <returns>A task that completes when the test's companies, cart and scope are gone.</returns>
    /// <exception cref="AggregateException">More than one step failed; holds each failure in step order.</exception>
    public async Task DisposeAsync()
    {
        if (_scope is null)
        {
            return;
        }

        var failures = new List<Exception>();
        try
        {
            await TearDownAsync();
        }
        catch (Exception tearDownFailure)
        {
            failures.Add(tearDownFailure);
        }

        await ReleaseScopeAsync(failures);
        ThrowFailures(failures, "The test tear-down failed.");
    }

    /// <summary>
    /// Builds the test's fixtures. Creates the company "Jade Software Corporation" through
    /// <see cref="TestCompanyBuilder.CreateCompanyAsync"/>, sets <see cref="TestCompanyBuilder.MyCompany"/> and binds it
    /// with <see cref="TestCompanyBuilder.Bind"/>. Ports <c>setUp</c>: <c>self.myCompany := self.createCompany()</c> and
    /// <c>app.initializeUnitTest(self.myCompany)</c>.
    /// </summary>
    /// <remarks>
    /// An override that builds another company sets <see cref="TestCompanyBuilder.MyCompany"/> to it, as
    /// <see cref="TestCompanyBuilder.CreateTestDataAsync"/> does.
    /// </remarks>
    /// <returns>A task that completes when the fixtures exist.</returns>
    /// <exception cref="InvalidOperationException">The company was not persisted.</exception>
    protected virtual async Task SetUpAsync()
    {
        Company company = await Builder.CreateCompanyAsync();
        Builder.MyCompany = company;
        Builder.Bind(company.Id);
    }

    /// <summary>
    /// Deletes the test's companies in a new scope of <see cref="Api"/>, each through
    /// <see cref="TestCompanyBuilder.DeleteCompanyAsync"/> with everything it owns: first
    /// <see cref="TestCompanyBuilder.MyCompany"/> when it is set, then every company still in
    /// <see cref="IErewhonDbContext.Companies"/>, in ascending id order. A company that no longer exists is skipped.
    /// Ports <c>tearDown</c>/<c>afterTest</c>: <c>delete self.myCompany</c>. Runs first in <see cref="DisposeAsync"/>.
    /// </summary>
    /// <returns>A task that completes when no company is left.</returns>
    /// <exception cref="InvalidOperationException">A delete failed; the message holds its full error text.</exception>
    protected virtual async Task TearDownAsync()
    {
        await DeleteCompaniesAsync();
    }

    /// <summary>
    /// Starts a further Erewhon API on Kestrel through <see cref="ErewhonApiHost.StartAsync"/> over
    /// <see cref="Database"/>, with <see cref="TestSessionContext.Register"/>, the configuration overrides in
    /// <paramref name="settings"/> and <paramref name="logSink"/>, and every other <see cref="ErewhonApiHostOptions"/>
    /// default. The caller disposes the host.
    /// </summary>
    /// <param name="settings">Configuration values that replace the host's defaults or add further keys.</param>
    /// <param name="logSink">A sink that receives every event of the host's logger, or <see langword="null"/> for none.</param>
    /// <returns>The running host.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Called before <see cref="InitializeAsync"/>.</exception>
    protected async Task<ErewhonApiHost> StartDedicatedHostAsync(
        IReadOnlyDictionary<string, string?> settings,
        CollectingLogSink? logSink = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return await ErewhonApiHost.StartAsync(new ErewhonApiHostOptions
        {
            ConnectionString = Database.ConnectionString,
            Settings = settings,
            LogSink = logSink,
            ConfigureServices = TestSessionContext.Register,
        });
    }

    /// <summary>
    /// Returns the shared host of <paramref name="assembly"/>, starting it on the first call; every call for the same
    /// assembly returns the same task.
    /// </summary>
    private static Task<SharedHost> GetSharedHostAsync(Assembly assembly) =>
        Hosts.GetOrAdd(
            assembly,
            key => new Lazy<Task<SharedHost>>(
                () => StartSharedHostAsync(key),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>
    /// Recreates and migrates the database of <paramref name="assembly"/> once in this process, starts the API over it
    /// with <see cref="TestSessionContext.Register"/> and a new <see cref="CollectingLogSink"/>, and registers
    /// <see cref="StopAtProcessExit"/> for the host on <see cref="AppDomain.ProcessExit"/>.
    /// </summary>
    private static async Task<SharedHost> StartSharedHostAsync(Assembly assembly)
    {
        PostgresDatabase database = PostgresDatabase.ForAssembly(assembly);
        await database.EnsureCreatedAndMigratedAsync();

        var logSink = new CollectingLogSink();
        ErewhonApiHost host = await ErewhonApiHost.StartAsync(new ErewhonApiHostOptions
        {
            ConnectionString = database.ConnectionString,
            LogSink = logSink,
            ConfigureServices = TestSessionContext.Register,
        });

        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopAtProcessExit(host);

        return new SharedHost(database, host, logSink);
    }

    /// <summary>
    /// Stops and then disposes <paramref name="host"/>, waiting at most <see cref="ProcessExitStopTimeout"/>. A stop still
    /// running when the wait ends is written to the standard error stream as one line with the host's
    /// <see cref="ErewhonApiHost.BaseAddress"/> and the wait. A failure is written there as one line with the
    /// <see cref="ErewhonApiHost.BaseAddress"/>, the full name of the failure's type and, when it has one, its JADE error
    /// number or SQLSTATE, as <see cref="DescribeStopFailure"/> returns them; the failure's message, inner exceptions and
    /// data are not written.
    /// </summary>
    private static void StopAtProcessExit(ErewhonApiHost host)
    {
        Task stop = Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(ProcessExitStopTimeout);
            try
            {
                await host.StopAsync(timeout.Token);
            }
            finally
            {
                await host.DisposeAsync();
            }
        });

        try
        {
            if (!stop.Wait(ProcessExitStopTimeout))
            {
                Console.Error.WriteLine(
                    $"The shared Erewhon API host at {host.BaseAddress} did not stop within {ProcessExitStopTimeout} of process exit.");
            }
        }
        catch (AggregateException stopFailure)
        {
            Console.Error.WriteLine(
                $"The shared Erewhon API host at {host.BaseAddress} failed to stop at process exit: {DescribeStopFailure(stopFailure.InnerException ?? stopFailure)}.");
        }
    }

    /// <summary>
    /// Returns the full name of the type of <paramref name="failure"/>, followed by <c>(JADE error &lt;n&gt;)</c> with the
    /// <see cref="IJadeException.ErrorCode"/> when <paramref name="failure"/> is an <see cref="IJadeException"/>, or else
    /// by <c>(SQLSTATE &lt;code&gt;)</c> when <see cref="PostgresErrorMapper.GetSqlState"/> finds a SQLSTATE of five ASCII
    /// letters or digits; for example <c>Npgsql.NpgsqlException (SQLSTATE 08006)</c>. No message, inner exception or data
    /// of <paramref name="failure"/> is included.
    /// </summary>
    private static string DescribeStopFailure(Exception failure)
    {
        Type failureType = failure.GetType();
        string typeName = failureType.FullName ?? failureType.Name;

        if (failure is IJadeException jade)
        {
            return $"{typeName} (JADE error {jade.ErrorCode.ToString(CultureInfo.InvariantCulture)})";
        }

        return PostgresErrorMapper.GetSqlState(failure) is { Length: 5 } sqlState && sqlState.All(char.IsAsciiLetterOrDigit)
            ? $"{typeName} (SQLSTATE {sqlState})"
            : typeName;
    }

    /// <summary>Returns the exception for <paramref name="member"/> read before <see cref="InitializeAsync"/>.</summary>
    private static InvalidOperationException NotInitialized(string member) =>
        new($"{member} is available once {nameof(InitializeAsync)} has run.");

    /// <summary>
    /// Does nothing when <paramref name="failures"/> is empty, rethrows its only failure unchanged, or throws an
    /// <see cref="AggregateException"/> with <paramref name="message"/> holding every failure in order.
    /// </summary>
    private static void ThrowFailures(List<Exception> failures, string message)
    {
        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(message, failures);
        }
    }

    /// <summary>
    /// Deletes <see cref="TestCompanyBuilder.MyCompany"/> when it is set, then every remaining company in ascending id
    /// order, each through <see cref="TestCompanyBuilder.DeleteCompanyAsync"/> in one new scope of <see cref="Api"/>.
    /// </summary>
    private async Task DeleteCompaniesAsync()
    {
        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        var builder = new TestCompanyBuilder(scope.ServiceProvider);

        if (_builder?.MyCompany is { } myCompany)
        {
            await builder.DeleteCompanyAsync(myCompany.Id);
        }

        long[] remainingCompanyIds = await scope.ServiceProvider
            .GetRequiredService<IErewhonDbContext>()
            .Companies
            .AsNoTracking()
            .OrderBy(company => company.Id)
            .Select(company => company.Id)
            .ToArrayAsync();

        foreach (long companyId in remainingCompanyIds)
        {
            await builder.DeleteCompanyAsync(companyId);
        }
    }

    /// <summary>
    /// Removes the cart of <see cref="Session"/> from <see cref="ICartStore"/> when the session was taken, then disposes
    /// the test's scope, adding each failure to <paramref name="failures"/> and running both steps. Afterwards no test
    /// scope is open.
    /// </summary>
    private async Task ReleaseScopeAsync(List<Exception> failures)
    {
        if (_scope is not { } scope)
        {
            return;
        }

        _scope = null;

        if (_session?.SessionId is { } sessionId)
        {
            try
            {
                scope.ServiceProvider.GetRequiredService<ICartStore>().RemoveCart(sessionId);
            }
            catch (Exception removeFailure)
            {
                failures.Add(removeFailure);
            }
        }

        try
        {
            await scope.DisposeAsync();
        }
        catch (Exception disposeFailure)
        {
            failures.Add(disposeFailure);
        }
    }

    /// <summary>The assembly database, the API running over it and the API's log sink, shared by an assembly's tests.</summary>
    /// <param name="Database">The assembly database.</param>
    /// <param name="Host">The API running over <paramref name="Database"/>.</param>
    /// <param name="LogSink">The sink receiving every event of <paramref name="Host"/>'s logger.</param>
    private sealed record SharedHost(PostgresDatabase Database, ErewhonApiHost Host, CollectingLogSink LogSink);
}
