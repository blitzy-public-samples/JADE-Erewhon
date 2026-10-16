using System.Net.Http.Json;
using System.Text.Json;
using Erewhon.Api.Rest.Contracts;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Notifications;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Erewhon.Persistence;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Api.Tests.Fixtures;

/// <summary>
/// Characterises the clean-up of <see cref="ErewhonTestBase"/> on PostgreSQL when the test scope still holds an open
/// transaction that created a country of the bound fixture company through <see cref="CountryService"/> in
/// <see cref="TransactionMode.Ambient"/> mode: after a failed set-up or a failed test no company and no uncommitted
/// country is left, and the next fixture's HTTP requests are answered with that fixture's own company. Each case drives
/// probe subclasses of <see cref="ErewhonTestBase"/> on the shared API of the test assembly through their
/// <see cref="IAsyncLifetime"/> members.
/// </summary>
public sealed class ErewhonTestBaseLifecycleTests : IAsyncLifetime
{
    private const string CompanyPath = "/api/rest/company";

    private const string UncommittedCountryName = "Lifecycle Uncommitted Country";

    private const int Ok = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly List<ErewhonTestBase> _probes = new();

    /// <summary>The point at which the test scope's transaction is left open.</summary>
    public enum FailurePoint
    {
        /// <summary>
        /// The set-up commits and binds the fixture company, creates the country in an open transaction of the test
        /// scope and then throws.
        /// </summary>
        SetUp,

        /// <summary>
        /// The test body creates the country in an open transaction of the test scope once the set-up has completed,
        /// and ends without committing or rolling it back.
        /// </summary>
        Test,
    }

    /// <summary>Each point at which the test scope's transaction is left open.</summary>
    public static TheoryData<FailurePoint> FailurePoints => new()
    {
        FailurePoint.SetUp,
        FailurePoint.Test,
    };

    /// <summary>Completes without work; each case opens its own probes.</summary>
    /// <returns>A completed task.</returns>
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Ends every probe the case opened, in the order they were opened, then initialises and ends a probe whose set-up
    /// builds nothing, whose tear-down deletes every company. Every step runs even when an earlier one fails; the
    /// failures are thrown as one <see cref="AggregateException"/> in step order.
    /// </summary>
    /// <returns>A task that completes when no probe scope is open and no company is left.</returns>
    /// <exception cref="AggregateException">A step failed; holds each failure in step order.</exception>
    public async Task DisposeAsync()
    {
        var failures = new List<Exception>();
        foreach (ErewhonTestBase probe in _probes)
        {
            try
            {
                await probe.DisposeAsync();
            }
            catch (Exception disposeFailure)
            {
                failures.Add(disposeFailure);
            }
        }

        try
        {
            var cleanup = new CleanupProbe();
            await cleanup.InitializeAsync();
            await cleanup.DisposeAsync();
        }
        catch (Exception cleanupFailure)
        {
            failures.Add(cleanupFailure);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("The lifecycle test clean-up failed.", failures);
        }
    }

    /// <summary>
    /// The clean-up of a test scope whose transaction holds an uncommitted country of the fixture company, left open at
    /// <paramref name="failurePoint"/>, leaves no company and no such country:
    /// for <see cref="FailurePoint.SetUp"/>, <see cref="ErewhonTestBase.InitializeAsync"/> throws the set-up's own
    /// exception instance; for <see cref="FailurePoint.Test"/>, the scope has an active transaction and a queued change
    /// notification before <see cref="ErewhonTestBase.DisposeAsync"/>, which completes without throwing. The next
    /// fixture then initialises with exactly one company, <c>GET /api/rest/company</c> answers 200 with that company's
    /// GUID, and ending it leaves no company.
    /// </summary>
    /// <param name="failurePoint">The point at which the test scope's transaction is left open.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [MemberData(nameof(FailurePoints))]
    public async Task CleanUpWithOpenTransactionLeavesNoCompanyForNextFixture(FailurePoint failurePoint)
    {
        if (failurePoint == FailurePoint.SetUp)
        {
            FailingSetUpProbe failing = Open(new FailingSetUpProbe());

            InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                failing.InitializeAsync);

            Assert.Same(failing.Failure, thrown);
        }
        else
        {
            FixtureProbe failing = Open(new FixtureProbe());
            await failing.InitializeAsync();
            await failing.CreateCountryInOpenTransactionAsync(UncommittedCountryName);
            Assert.True(failing.HasActiveTransaction);
            Assert.NotEmpty(failing.PendingNotifications);

            Exception? tearDownFailure = await Record.ExceptionAsync(failing.DisposeAsync);

            Assert.Null(tearDownFailure);
        }

        Assert.Equal(0, await CountCompaniesAsync());
        Assert.False(await CountryExistsAsync(UncommittedCountryName));

        FixtureProbe next = Open(new FixtureProbe());
        await next.InitializeAsync();
        Company nextCompany = next.FixtureCompany;

        using (HttpClient http = next.CreateHttpClient())
        using (HttpResponseMessage response = await http.GetAsync(CompanyPath))
        {
            Assert.Equal(Ok, (int)response.StatusCode);
            CompanyDto? dtoCompany = await response.Content.ReadFromJsonAsync<CompanyDto>(Json);
            Assert.NotNull(dtoCompany);
            Assert.Equal(nextCompany.Guid, dtoCompany.Guid);
        }

        Assert.Equal(1, await CountCompaniesAsync());

        await next.DisposeAsync();

        Assert.Equal(0, await CountCompaniesAsync());
    }

    /// <summary>Adds <paramref name="probe"/> to the probes <see cref="DisposeAsync"/> ends and returns it.</summary>
    private TProbe Open<TProbe>(TProbe probe)
        where TProbe : ErewhonTestBase
    {
        _probes.Add(probe);
        return probe;
    }

    /// <summary>Returns the number of committed companies, read in a new context over the assembly database.</summary>
    private static async Task<int> CountCompaniesAsync()
    {
        await using ErewhonDbContext db = CreateContext();
        return await db.Companies.CountAsync();
    }

    /// <summary>
    /// Returns whether a committed country named <paramref name="name"/> exists, read in a new context over the
    /// assembly database.
    /// </summary>
    private static async Task<bool> CountryExistsAsync(string name)
    {
        await using ErewhonDbContext db = CreateContext();
        return await db.Countries.AnyAsync(country => country.Name == name);
    }

    /// <summary>Returns a new context over the assembly database with no change-notification interceptor.</summary>
    private static ErewhonDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ErewhonDbContext>()
            .UseNpgsql(PostgresDatabase.ForAssembly(typeof(ErewhonTestBaseLifecycleTests).Assembly).ConnectionString)
            .Options);

    /// <summary>
    /// A test of <see cref="ErewhonTestBase"/> with the default set-up, which commits the company "Jade Software
    /// Corporation" and binds it to the test scope.
    /// </summary>
    private class FixtureProbe : ErewhonTestBase
    {
        /// <summary>The fixture company the set-up committed.</summary>
        /// <exception cref="InvalidOperationException">The set-up has not committed a company.</exception>
        public Company FixtureCompany =>
            Builder.MyCompany ?? throw new InvalidOperationException("The set-up has not committed a company.");

        /// <summary>Whether the test scope's unit of work has an active transaction.</summary>
        public bool HasActiveTransaction => GetService<IUnitOfWork>().HasActiveTransaction;

        /// <summary>The change notifications queued in the test scope, in queue order.</summary>
        public IReadOnlyList<ChangeEvent> PendingNotifications => GetService<NotificationQueue>().Pending;

        /// <summary>Returns a new HTTP client addressed to the shared API of the test assembly.</summary>
        /// <returns>The client; the caller disposes it.</returns>
        public HttpClient CreateHttpClient() => Api.CreateHttpClient();

        /// <summary>
        /// Begins a transaction through the test scope's <see cref="IUnitOfWork"/>, then creates a country named
        /// <paramref name="name"/> in the bound company through the scope's <see cref="CountryService"/> in
        /// <see cref="TransactionMode.Ambient"/> mode. The transaction stays open.
        /// </summary>
        /// <param name="name">The country name.</param>
        /// <returns>A task that completes when the country is written in the open transaction.</returns>
        /// <exception cref="InvalidOperationException">The country was not written; the message holds the full error text.</exception>
        public async Task CreateCountryInOpenTransactionAsync(string name)
        {
            await GetService<IUnitOfWork>().BeginAsync();

            OperationResult result = await GetService<CountryService>().ExecuteAsync(
                new CountryCommand { Name = name },
                TransactionType.Persist,
                TransactionMode.Ambient);

            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"Country \"{name}\" was not written: {result.FullErrorText}");
            }
        }
    }

    /// <summary>
    /// A test whose set-up commits and binds the default fixture company, creates the country
    /// <see cref="UncommittedCountryName"/> in an open transaction of the test scope and then throws
    /// <see cref="Failure"/>.
    /// </summary>
    private sealed class FailingSetUpProbe : FixtureProbe
    {
        /// <summary>The exception the set-up throws.</summary>
        public InvalidOperationException Failure { get; } =
            new("The lifecycle probe set-up failed with its transaction open.");

        /// <summary>
        /// Commits and binds the default fixture company, creates the country <see cref="UncommittedCountryName"/> in
        /// an open transaction of the test scope, then throws <see cref="Failure"/>.
        /// </summary>
        /// <returns>A task that faults with <see cref="Failure"/>.</returns>
        protected override async Task SetUpAsync()
        {
            await base.SetUpAsync();
            await CreateCountryInOpenTransactionAsync(UncommittedCountryName);
            throw Failure;
        }
    }

    /// <summary>A test whose set-up builds nothing and whose tear-down deletes every company.</summary>
    private sealed class CleanupProbe : ErewhonTestBase
    {
        /// <summary>Completes without building a fixture.</summary>
        /// <returns>A completed task.</returns>
        protected override Task SetUpAsync() => Task.CompletedTask;
    }
}
