using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Erewhon.Api.App.Contracts;
using Erewhon.Api.Errors;
using Erewhon.Api.Sessions;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Erewhon.Persistence;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Serilog.Events;

namespace Erewhon.Api.Tests.CrossCutting;

/// <summary>
/// Characterises, over HTTP against the running Erewhon API and on further connections to the PostgreSQL test
/// database, the item-code allocation lock, the serialisation of checkouts and the three lock outcomes: a
/// transaction-agent lock not obtained, a closure row lock not obtained and a deadlock. Each test starts with the
/// fixture company bound, with the agent "John Smith", the clients "Clive Entworth" and "Clissa Entworth", the country
/// "France" with its region "Paris" and the item category "Abstractions", and ends with no company and without the
/// sessions it opened. A lock held by the test is held on an unpooled connection of its own and released, or its
/// transaction rolled back, before the test ends. Tests that shorten a lock wait run against a further API over the
/// same database whose <c>Erewhon:Locks:*</c> settings and log sink are their own.
/// </summary>
public sealed class ConcurrencyTests : ErewhonTestBase
{
    private const string SessionsPath = "api/app/sessions";
    private const string AgentsPath = "api/app/admin/agents";
    private const string RetailItemsPath = "api/app/admin/retail-items";
    private const string CartItemsPath = "api/app/shop/cart/items";
    private const string CheckoutPath = "api/app/shop/checkout";
    private const string CloseNowPath = "api/app/admin/tenders/close-now";

    private const string JsonMediaType = "application/json";

    private const string LockTimeoutKey = "Erewhon:Locks:TimeoutMs";
    private const string AllocationTimeoutKey = "Erewhon:Locks:AllocationTimeoutMs";

    private const int ShortLockWaitMs = 500;
    private const int ShortAllocationWaitMs = 200;
    private const int DefaultLockWaitMs = 10000;
    private const int DeadlockLockWaitFactor = 10;
    private const int ParallelCreates = 8;
    private const int ExplicitCodeNumber = 9001;
    private const int TimerToleranceMs = 50;

    private const string RequestLoggingSourceContext = "Serilog.AspNetCore.RequestLoggingMiddleware";
    private const string SourceContextProperty = "SourceContext";
    private const string EventIdProperty = "EventId";
    private const string EventIdIdProperty = "Id";

    private const string ItemsCodeNumberIndex = "ix_items_company_id_code_number";

    private const string SecondClientName = "Clissa Entworth";
    private const string AgentName = "John Smith";
    private const string NewAgentName = "Jane Doe";

    private const string Street = "314 Nohwere Lane";
    private const string City = "Christchurch";
    private const string CountryName = "New Zealand";
    private const string Phone = "555 1234567";
    private const string Fax = "FAX";
    private const string Email = "admin@Erewhon.nz";
    private const string WebSite = "www.jadeworld.com";

    private const string ItemDescription = "An item created while its locks are contended";
    private const decimal ItemPrice = 100.00m;
    private const decimal TenderReserve = 5.50m;

    private const string ParallelItemNamePrefix = "Parallel ";
    private const string BodyTextMarker = "\u0001";

    private const string DeadlockTimeoutSql = "SELECT setting::int FROM pg_settings WHERE name = 'deadlock_timeout'";

    private const string WaitingAdvisoryLockSql =
        "SELECT count(*) FROM pg_locks AS l JOIN pg_database AS d ON d.oid = l.database "
        + "WHERE d.datname = current_database() AND l.locktype = 'advisory' AND NOT l.granted AND l.objsubid = 1 "
        + "AND ((l.classid::bigint << 32) | l.objid::bigint) = hashtextextended(@name, 0)";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly TimeSpan MinimumLockWait = TimeSpan.FromMilliseconds(ShortLockWaitMs - (2 * TimerToleranceMs));

    private static readonly TimeSpan MaximumLockWait = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan MinimumAllocationExhaustion = TimeSpan.FromMilliseconds(
        (ItemCodeAllocator.MaxAttempts * ShortAllocationWaitMs)
        + ((ItemCodeAllocator.MaxAttempts - 1) * ItemCodeAllocator.RetryDelayMilliseconds)
        - TimerToleranceMs);

    private static readonly TimeSpan MaximumAllocationExhaustion = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    private static readonly TimeSpan WaitDeadline = TimeSpan.FromSeconds(10);

    private readonly List<string> _sharedSessionIds = new();

    private Client? _secondClient;

    private Company FixtureCompany => Fixture(Builder.MyCompany, nameof(TestCompanyBuilder.MyCompany));

    private long CompanyId => FixtureCompany.Id;

    private Agent FixtureAgent => Fixture(Builder.MyAgent, nameof(TestCompanyBuilder.MyAgent));

    private Client FirstClient => Fixture(Builder.MyClient, nameof(TestCompanyBuilder.MyClient));

    private Client SecondClient => Fixture(_secondClient, nameof(_secondClient));

    private Region FixtureRegion => Fixture(Builder.MyRegion, nameof(TestCompanyBuilder.MyRegion));

    private ItemCategory FixtureCategory => Fixture(Builder.MyItemCategory, nameof(TestCompanyBuilder.MyItemCategory));

    /// <summary>
    /// Eight concurrent <c>POST /api/app/admin/retail-items</c> requests of one administrator session, each with code
    /// number 0, answer either 200 with a succeeded <see cref="OperationResult"/> or 500 with the exception
    /// <see cref="ErrorOutcome"/> headed "Error 23505"; at least one succeeds. Afterwards no two items of the company
    /// share a code number, and the company holds exactly one item per succeeded request, each with the id its result
    /// reported.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ParallelItemCreatesNeverPersistDuplicateNumbers()
    {
        using HttpClient admin = await OpenAdministratorSessionAsync(Api);

        HttpOutcome[] outcomes = await Task.WhenAll(
            Enumerable.Range(1, ParallelCreates)
                .Select(number => SendAsync(
                    admin,
                    HttpMethod.Post,
                    RetailItemsPath,
                    RetailItemRequestFor(ParallelItemNamePrefix + number.ToString(CultureInfo.InvariantCulture), 0))));

        var succeededIds = new List<long>();
        foreach (HttpOutcome outcome in outcomes)
        {
            if (outcome.StatusCode == HttpStatusCode.OK)
            {
                OperationResult result = ReadOperationResult(outcome);
                Assert.True(result.Succeeded, result.FullErrorText);
                succeededIds.Add(Assert.NotNull(result.Id));
            }
            else
            {
                ErrorOutcome error = ReadErrorOutcome(outcome);
                Assert.Equal(ErrorOutcome.KindException, error.Kind);
                Assert.Equal(
                    MessageCatalog.Get(MessageKeys.BaseExceptionHeading, PostgresErrorMapper.UniqueViolation),
                    error.Heading);
            }
        }

        Assert.NotEmpty(succeededIds);

        IReadOnlyList<StoredItem> items = await ReadItemsAsync();
        Assert.DoesNotContain(items.GroupBy(item => item.CodeNumber), group => group.Count() > 1);

        List<StoredItem> parallelItems = items
            .Where(item => item.Name.StartsWith(ParallelItemNamePrefix, StringComparison.Ordinal))
            .ToList();
        Assert.Equal(succeededIds.Order(), parallelItems.Select(item => item.Id).Order());
        Assert.Equal(parallelItems.Count, parallelItems.Select(item => item.CodeNumber).Distinct().Count());
    }

    /// <summary>
    /// While another session holds the session-level advisory lock <c>Company.allItems:&lt;companyId&gt;</c>, a
    /// <c>POST /api/app/admin/retail-items</c> with code number 0 on an API whose
    /// <c>Erewhon:Locks:AllocationTimeoutMs</c> is 200 answers 500 with the exception <see cref="ErrorOutcome"/>
    /// headed "Error 500001" whose body is <c>$BaseExceptionBodyWeb</c> with 500001 and
    /// <c>$LockNotObtainedException</c>, after at least three waits of 200 ms and two pauses of 100 ms, less 50 ms, and
    /// within 10 s. One exception log entry is written, and no item is stored.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task CodeAllocationLockExhaustionRaises500001()
    {
        var sink = new CollectingLogSink();
        await using ErewhonApiHost host = await StartDedicatedHostAsync(
            new Dictionary<string, string?>
            {
                [AllocationTimeoutKey] = ShortAllocationWaitMs.ToString(CultureInfo.InvariantCulture),
            },
            sink);
        using HttpClient admin = await OpenAdministratorSessionAsync(host);

        string allocationLock = LockNames.CompanyAllItems(CompanyId);
        await using LockHolder holder = await LockHolder.OpenAsync(Database.ConnectionString);
        await holder.HoldSessionLockAsync(allocationLock);
        try
        {
            sink.Clear();

            HttpOutcome response = await SendAsync(
                admin,
                HttpMethod.Post,
                RetailItemsPath,
                RetailItemRequestFor("Exhausted Allocation", 0));

            string code = ErewhonException.LockNotObtained.ToString(CultureInfo.InvariantCulture);
            Assert.Equal(
                new ErrorOutcome(
                    ErrorOutcome.KindException,
                    MessageCatalog.Get(MessageKeys.BaseExceptionHeading, code),
                    MessageCatalog.Get(
                        MessageKeys.BaseExceptionBodyWeb,
                        code,
                        MessageCatalog.Get(MessageKeys.LockNotObtainedException),
                        MessageCatalog.Get(MessageKeys.BaseNotAvailable),
                        string.Empty)),
                ReadErrorOutcome(response));
            Assert.InRange(response.Elapsed, MinimumAllocationExhaustion, MaximumAllocationExhaustion);
            Assert.Single(ExceptionLogEntries(sink));
        }
        finally
        {
            await holder.ReleaseSessionLockAsync(allocationLock);
        }

        Assert.Empty(await ReadItemsAsync());
    }

    /// <summary>
    /// Two shop sessions, for "Clive Entworth" and "Clissa Entworth", each add the same retail item to their cart and
    /// check out at the same time. Exactly one sale of the item is stored, to the client of the checkout that
    /// answered 200 with a succeeded result. The other checkout answers 200 with a failed result whose full error text
    /// holds <c>$ItemSold</c>, or 500 with the exception <see cref="ErrorOutcome"/> headed "Error 23505".
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task CheckoutsSerialise()
    {
        RetailItem item = await Builder.CreateTestRetailItemAsync(
            "Contested Item",
            ItemDescription,
            null,
            FixtureRegion,
            FixtureCategory,
            FixtureAgent,
            ItemPrice);

        using HttpClient first = await OpenShopSessionAsync(Api, FirstClient);
        using HttpClient second = await OpenShopSessionAsync(Api, SecondClient);
        await AddToCartAsync(first, item.Id);
        await AddToCartAsync(second, item.Id);

        HttpOutcome[] checkouts = await Task.WhenAll(
            SendAsync(first, HttpMethod.Post, CheckoutPath, null),
            SendAsync(second, HttpMethod.Post, CheckoutPath, null));
        long[] clientIds = [FirstClient.Id, SecondClient.Id];

        int winner = Assert.Single(
            Enumerable.Range(0, checkouts.Length),
            index => checkouts[index].StatusCode == HttpStatusCode.OK
                && ReadCheckout(checkouts[index]).Result.Succeeded);
        HttpOutcome loser = checkouts[1 - winner];

        if (loser.StatusCode == HttpStatusCode.OK)
        {
            OperationResult result = ReadCheckout(loser).Result;
            Assert.False(result.Succeeded);
            Assert.Contains(MessageCatalog.Get(MessageKeys.ItemSold), result.FullErrorText, StringComparison.Ordinal);
        }
        else
        {
            ErrorOutcome error = ReadErrorOutcome(loser);
            Assert.Equal(ErrorOutcome.KindException, error.Kind);
            Assert.Equal(
                MessageCatalog.Get(MessageKeys.BaseExceptionHeading, PostgresErrorMapper.UniqueViolation),
                error.Heading);
        }

        Assert.Equal(clientIds[winner], Assert.Single(await ReadSaleClientIdsAsync(item.Id)));
    }

    /// <summary>
    /// While another session holds the advisory lock <c>Company.allAgents:&lt;companyId&gt;</c>, on an API whose
    /// <c>Erewhon:Locks:TimeoutMs</c> is 500: creating the agent "Jane Doe" answers 200 with a failed result whose only
    /// error is <c>$CreateConflictedWithAnotherUser</c>; renaming "John Smith" to "Jane Doe" answers 200 with only
    /// <c>$UpdateConflictedWithAnotherUser</c>; deleting "John Smith" answers 200 with only
    /// <c>$DeleteConflictedWithAnotherUser</c>; each after at least 400 ms and within 5 s. The company's agents are
    /// still "John Smith" alone, and no exception log entry is written. Once the lock is released, the same create
    /// succeeds.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task CreateUpdateDeleteConflict()
    {
        var sink = new CollectingLogSink();
        await using ErewhonApiHost host = await StartDedicatedHostAsync(
            new Dictionary<string, string?> { [LockTimeoutKey] = ShortLockWaitMs.ToString(CultureInfo.InvariantCulture) },
            sink);
        using HttpClient admin = await OpenAdministratorSessionAsync(host);

        string agentPath = $"{AgentsPath}/{FixtureAgent.Id.ToString(CultureInfo.InvariantCulture)}";
        string agentsLock = LockNames.CompanyAllAgents(CompanyId);
        await using LockHolder holder = await LockHolder.OpenAsync(Database.ConnectionString);
        await holder.HoldSessionLockAsync(agentsLock);
        try
        {
            sink.Clear();

            HttpOutcome create = await SendAsync(admin, HttpMethod.Post, AgentsPath, AgentRequestFor(NewAgentName));
            AssertConflict(create, MessageKeys.CreateConflictedWithAnotherUser);

            HttpOutcome update = await SendAsync(admin, HttpMethod.Put, agentPath, AgentRequestFor(NewAgentName));
            AssertConflict(update, MessageKeys.UpdateConflictedWithAnotherUser);

            HttpOutcome delete = await SendAsync(admin, HttpMethod.Delete, agentPath, null);
            AssertConflict(delete, MessageKeys.DeleteConflictedWithAnotherUser);

            Assert.Equal(new[] { AgentName }, await ReadAgentNamesAsync());
            Assert.Empty(ExceptionLogEntries(sink));
        }
        finally
        {
            await holder.ReleaseSessionLockAsync(agentsLock);
        }

        OperationResult created = ReadOperationResult(
            await SendAsync(admin, HttpMethod.Post, AgentsPath, AgentRequestFor(NewAgentName)));
        Assert.True(created.Succeeded, created.FullErrorText);
        Assert.Equal(new[] { NewAgentName, AgentName }, await ReadAgentNamesAsync());
    }

    /// <summary>
    /// With the tender items A, closing the day after the server date, and B, closing two days after it, each holding
    /// one tender, and B's <c>items</c> row held with <c>FOR UPDATE</c> by another transaction:
    /// <c>POST /api/app/admin/tenders/close-now</c> for the date two days after the server date, on an API whose
    /// <c>Erewhon:Locks:TimeoutMs</c> is 500, answers 500 with the lock <see cref="ErrorOutcome"/>, headed
    /// <c>$BaseLockMessageHeading</c> with the body <c>$BaseLockMessageBodyWeb</c>, after at least 400 ms and within
    /// 5 s. No exception log entry is written. A is sold through a tender sale of its tender, and B has no sale.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ClosureLockTimeoutGivesLockOutcomeWithoutLog()
    {
        var sink = new CollectingLogSink();
        await using ErewhonApiHost host = await StartDedicatedHostAsync(
            new Dictionary<string, string?> { [LockTimeoutKey] = ShortLockWaitMs.ToString(CultureInfo.InvariantCulture) },
            sink);
        using HttpClient admin = await OpenAdministratorSessionAsync(host);

        DateOnly serverDate = await GetService<IServerClock>().GetServerDateAsync();
        TenderItem closesFirst = await CreateTenderItemAsync("Closes First", serverDate.AddDays(1));
        TenderItem closesSecond = await CreateTenderItemAsync("Closes Second", serverDate.AddDays(2));
        Tender firstTender = await Builder.CreateTenderAsync(FirstClient, closesFirst, 10.00m);
        await Builder.CreateTenderAsync(SecondClient, closesSecond, 20.00m);

        await using LockHolder holder = await LockHolder.OpenAsync(Database.ConnectionString);
        await holder.BeginAsync();
        try
        {
            await holder.LockItemRowAsync(closesSecond.Id);
            sink.Clear();

            HttpOutcome response = await SendAsync(
                admin,
                HttpMethod.Post,
                CloseNowPath,
                new CloseNowRequest(serverDate.AddDays(2)));

            Assert.Equal(
                new ErrorOutcome(
                    ErrorOutcome.KindLock,
                    MessageCatalog.Get(MessageKeys.BaseLockMessageHeading),
                    MessageCatalog.Get(MessageKeys.BaseLockMessageBodyWeb, string.Empty, string.Empty, string.Empty)),
                ReadErrorOutcome(response));
            Assert.InRange(response.Elapsed, MinimumLockWait, MaximumLockWait);
            Assert.Empty(ExceptionLogEntries(sink));
        }
        finally
        {
            await holder.RollbackAsync();
        }

        Assert.Equal(firstTender.Id, Assert.Single(await ReadTenderSaleTenderIdsAsync(closesFirst.Id)));
        Assert.Empty(await ReadSaleClientIdsAsync(closesSecond.Id));
    }

    /// <summary>
    /// Another transaction holds the advisory lock <c>Agent.allItems:&lt;agentId&gt;</c>, the second lock a retail-item
    /// create takes. A <c>POST /api/app/admin/retail-items</c> with an explicit code number, on an API whose
    /// <c>Erewhon:Locks:TimeoutMs</c> is ten times <c>deadlock_timeout</c> and at least 10000, takes
    /// <c>Company.allItems:&lt;companyId&gt;</c> and waits for <c>Agent.allItems:&lt;agentId&gt;</c>; the other
    /// transaction then requests <c>Company.allItems:&lt;companyId&gt;</c>. The request answers 500 with the deadlock
    /// <see cref="ErrorOutcome"/>, headed <c>$BaseDeadlockHeading</c> with the body <c>$BaseDeadlockBodyWeb</c>, the
    /// other transaction's request is granted, exactly one exception log entry is written, and no item is stored.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DeadlockGivesDeadlockOutcomeAndLog()
    {
        int deadlockTimeoutMs = await ReadDeadlockTimeoutMsAsync();
        int lockWaitMs = Math.Max(DefaultLockWaitMs, deadlockTimeoutMs * DeadlockLockWaitFactor);
        var sink = new CollectingLogSink();
        await using ErewhonApiHost host = await StartDedicatedHostAsync(
            new Dictionary<string, string?> { [LockTimeoutKey] = lockWaitMs.ToString(CultureInfo.InvariantCulture) },
            sink);
        using HttpClient admin = await OpenAdministratorSessionAsync(host);

        string companyItemsLock = LockNames.CompanyAllItems(CompanyId);
        string agentItemsLock = LockNames.AgentAllItems(FixtureAgent.Id);
        Task<HttpOutcome>? create = null;
        Task? holderRequest = null;
        await using LockHolder holder = await LockHolder.OpenAsync(Database.ConnectionString);
        await holder.BeginAsync();
        try
        {
            await holder.HoldXactLockAsync(agentItemsLock);
            sink.Clear();

            create = SendAsync(
                admin,
                HttpMethod.Post,
                RetailItemsPath,
                RetailItemRequestFor("Deadlocked Item", ExplicitCodeNumber));
            await WaitUntilLockAwaitedAsync(agentItemsLock, create);
            holderRequest = holder.HoldXactLockAsync(companyItemsLock);

            HttpOutcome response = await create;
            await holderRequest;

            Assert.Equal(
                new ErrorOutcome(
                    ErrorOutcome.KindDeadlock,
                    MessageCatalog.Get(MessageKeys.BaseDeadlockHeading),
                    MessageCatalog.Get(MessageKeys.BaseDeadlockBodyWeb, string.Empty)),
                ReadErrorOutcome(response));
            Assert.Single(ExceptionLogEntries(sink));
        }
        finally
        {
            if (holderRequest is not null)
            {
                await Task.WhenAny(holderRequest);
            }

            await holder.RollbackAsync();

            if (create is not null)
            {
                await Task.WhenAny(create);
            }
        }

        Assert.Empty(await ReadItemsAsync());
    }

    /// <summary>
    /// A code number allocated through <see cref="ItemCodeAllocator.AllocateNextCodeNumberAsync"/> in a scope of the
    /// API is then taken by a competing retail item created through <see cref="ItemService"/>. A
    /// <c>POST /api/app/admin/retail-items</c> with the allocated number answers 500 with the exception
    /// <see cref="ErrorOutcome"/> headed "Error 23505", whose body is <c>$BaseExceptionBodyWeb</c> with 23505 and the
    /// PostgreSQL message naming the unique index <c>ix_items_company_id_code_number</c>. The competing item is the only
    /// item with that number.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task Q28_AllocationWindowGivesDatabaseLevelFailure()
    {
        using HttpClient admin = await OpenAdministratorSessionAsync(Api);

        int allocated;
        await using (AsyncServiceScope scope = Api.Services.CreateAsyncScope())
        {
            allocated = await scope.ServiceProvider
                .GetRequiredService<ItemCodeAllocator>()
                .AllocateNextCodeNumberAsync(CompanyId);
        }

        var competing = new RetailItemCommand
        {
            CodeNumber = allocated,
            Name = "Competing Item",
            Description = ItemDescription,
            Price = ItemPrice,
            AgentId = FixtureAgent.Id,
            ItemCategoryId = FixtureCategory.Id,
            RegionId = FixtureRegion.Id,
        };
        OperationResult competingResult = await GetService<ItemService>()
            .ExecuteAsync(competing, TransactionType.Persist, TransactionMode.Own);
        Assert.True(competingResult.Succeeded, competingResult.FullErrorText);

        HttpOutcome response = await SendAsync(
            admin,
            HttpMethod.Post,
            RetailItemsPath,
            RetailItemRequestFor("Allocated Item", allocated));

        ErrorOutcome outcome = ReadErrorOutcome(response);
        Assert.Equal(ErrorOutcome.KindException, outcome.Kind);
        Assert.Equal(
            MessageCatalog.Get(MessageKeys.BaseExceptionHeading, PostgresErrorMapper.UniqueViolation),
            outcome.Heading);
        AssertDatabaseFailureBody(outcome.Body, PostgresErrorMapper.UniqueViolation, ItemsCodeNumberIndex);

        StoredItem holderOfNumber = Assert.Single(await ReadItemsAsync(), item => item.CodeNumber == allocated);
        Assert.Equal(competingResult.Id, holderOfNumber.Id);
    }

    /// <summary>
    /// Inside a transaction begun on a scope's <see cref="IUnitOfWork"/>, with one retail item stored,
    /// <see cref="ItemCodeAllocator.AllocateNextCodeNumberAsync"/> returns the highest stored code number plus 1. The
    /// transaction is still active and a query through the scope's <see cref="IErewhonDbContext"/> still runs, and
    /// before the transaction ends another connection obtains and releases the session-level advisory lock
    /// <c>Company.allItems:&lt;companyId&gt;</c> without waiting.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AllocationLockReleasedWhileCallerTransactionOpen()
    {
        await Builder.CreateTestRetailItemAsync(
            "Numbered Item",
            ItemDescription,
            null,
            FixtureRegion,
            FixtureCategory,
            FixtureAgent,
            ItemPrice);
        int highest = (await ReadItemsAsync()).Max(item => item.CodeNumber);

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        ItemCodeAllocator allocator = scope.ServiceProvider.GetRequiredService<ItemCodeAllocator>();
        IErewhonDbContext db = scope.ServiceProvider.GetRequiredService<IErewhonDbContext>();
        long companyId = CompanyId;

        await unitOfWork.BeginAsync();
        try
        {
            int allocated = await allocator.AllocateNextCodeNumberAsync(companyId);

            Assert.Equal(highest + 1, allocated);
            Assert.True(unitOfWork.HasActiveTransaction);
            Assert.Equal(1, await db.Items.CountAsync(item => item.CompanyId == companyId));

            string allocationLock = LockNames.CompanyAllItems(companyId);
            await using LockHolder probe = await LockHolder.OpenAsync(Database.ConnectionString);
            Assert.True(await probe.TryHoldSessionLockAsync(allocationLock));
            Assert.True(await probe.ReleaseSessionLockAsync(allocationLock));
            Assert.True(unitOfWork.HasActiveTransaction);
        }
        finally
        {
            await unitOfWork.RollbackAsync();
        }
    }

    /// <summary>
    /// Builds the fixtures after the company of <see cref="ErewhonTestBase.SetUpAsync"/>: the agent "John Smith", the
    /// client "Clive Entworth", the client "Clissa Entworth" at 314 Nohwere Lane, the country "France" with its region
    /// "Paris" and the item category "Abstractions", each but the second client held in its <c>My*</c> property.
    /// </summary>
    /// <returns>A task that completes when the fixtures exist.</returns>
    protected override async Task SetUpAsync()
    {
        await base.SetUpAsync();

        Builder.MyAgent = await Builder.CreateAgentAsync();
        Builder.MyClient = await Builder.CreateClientAsync();
        _secondClient = await Builder.CreateTestClientAsync(
            SecondClientName,
            Builder.CreateTestAddress(Street, City, CountryName, Phone, Fax, Email, WebSite));
        Builder.MyCountry = await Builder.CreateCountryAsync();
        Builder.MyRegion = await Builder.CreateRegionAsync(Builder.MyCountry);
        Builder.MyItemCategory = await Builder.CreateItemCategoryAsync(
            "Abstractions",
            "ABST",
            "Items that do not model real world observables");
    }

    /// <summary>
    /// Removes every session the test opened on <see cref="ErewhonTestBase.Api"/> from its
    /// <see cref="SessionRegistry"/>, with its cart, then deletes the test's companies through the base tear-down.
    /// </summary>
    /// <returns>A task that completes when the sessions and companies are gone.</returns>
    protected override async Task TearDownAsync()
    {
        try
        {
            SessionRegistry registry = Api.Services.GetRequiredService<SessionRegistry>();
            foreach (string sessionId in _sharedSessionIds)
            {
                registry.Remove(sessionId);
            }
        }
        finally
        {
            await base.TearDownAsync();
        }
    }

    /// <summary>Returns <paramref name="value"/>, or throws when <see cref="SetUpAsync"/> has not set it.</summary>
    private static T Fixture<T>(T? value, string name)
        where T : class =>
        value ?? throw new InvalidOperationException($"The fixture {name} is created by {nameof(SetUpAsync)}.");

    /// <summary>
    /// Sends <paramref name="method"/> <paramref name="path"/>, with <paramref name="body"/> as JSON when it is not
    /// <see langword="null"/>, and returns the status, media type and body of the response with the time it took.
    /// </summary>
    private static async Task<HttpOutcome> SendAsync(HttpClient http, HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        var stopwatch = Stopwatch.StartNew();
        using HttpResponseMessage response = await http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        stopwatch.Stop();

        return new HttpOutcome(response.StatusCode, response.Content.Headers.ContentType?.MediaType, text, stopwatch.Elapsed);
    }

    /// <summary>Asserts status 200 and returns the body read as an <see cref="OperationResult"/>.</summary>
    private static OperationResult ReadOperationResult(HttpOutcome outcome)
    {
        Assert.Equal(HttpStatusCode.OK, outcome.StatusCode);
        return Deserialize<OperationResult>(outcome.Body);
    }

    /// <summary>Asserts status 200 and returns the body read as a <see cref="CheckoutResponse"/>.</summary>
    private static CheckoutResponse ReadCheckout(HttpOutcome outcome)
    {
        Assert.Equal(HttpStatusCode.OK, outcome.StatusCode);
        return Deserialize<CheckoutResponse>(outcome.Body);
    }

    /// <summary>
    /// Asserts status 500 with an <c>application/json</c> body and returns the body read as an
    /// <see cref="ErrorOutcome"/>.
    /// </summary>
    private static ErrorOutcome ReadErrorOutcome(HttpOutcome outcome)
    {
        Assert.Equal(HttpStatusCode.InternalServerError, outcome.StatusCode);
        Assert.Equal(JsonMediaType, outcome.MediaType);
        return Deserialize<ErrorOutcome>(outcome.Body);
    }

    /// <summary>Returns <paramref name="body"/> read as <typeparamref name="T"/>, asserting it is not JSON null.</summary>
    private static T Deserialize<T>(string body)
        where T : class
    {
        T? value = JsonSerializer.Deserialize<T>(body, Json);
        Assert.NotNull(value);
        return value;
    }

    /// <summary>
    /// Asserts status 200 with a failed <see cref="OperationResult"/> whose only error is the message
    /// <paramref name="messageKey"/>, answered after at least 400 ms and within 5 s.
    /// </summary>
    private static void AssertConflict(HttpOutcome outcome, string messageKey)
    {
        OperationResult result = ReadOperationResult(outcome);
        Assert.False(result.Succeeded);
        Assert.Equal(new[] { MessageCatalog.Get(messageKey) }, result.Errors);
        Assert.InRange(outcome.Elapsed, MinimumLockWait, MaximumLockWait);
    }

    /// <summary>
    /// Asserts that <paramref name="body"/> is <c>$BaseExceptionBodyWeb</c> with the error code
    /// <paramref name="sqlState"/> and, as its error text, a PostgreSQL message that names <paramref name="indexName"/>.
    /// </summary>
    private static void AssertDatabaseFailureBody(string body, string sqlState, string indexName)
    {
        string template = MessageCatalog.Get(
            MessageKeys.BaseExceptionBodyWeb,
            sqlState,
            BodyTextMarker,
            MessageCatalog.Get(MessageKeys.BaseNotAvailable),
            string.Empty);
        int marker = template.IndexOf(BodyTextMarker, StringComparison.Ordinal);
        string prefix = template[..marker];
        string suffix = template[(marker + BodyTextMarker.Length)..];

        Assert.StartsWith(prefix, body, StringComparison.Ordinal);
        Assert.EndsWith(suffix, body, StringComparison.Ordinal);
        Assert.Contains($"({sqlState})", body, StringComparison.Ordinal);
        Assert.Contains(indexName, body[prefix.Length..^suffix.Length], StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the error and fatal events of <paramref name="sink"/>, other than the HTTP request events of the Serilog
    /// request logging, that have the event id <see cref="ErrorOutcomeMiddleware.UnhandledExceptionEventId"/> or carry
    /// an exception.
    /// </summary>
    private static IReadOnlyList<LogEvent> ExceptionLogEntries(CollectingLogSink sink) =>
        sink.ErrorEvents
            .Where(logEvent => !IsRequestLoggingEvent(logEvent)
                && (IsUnhandledExceptionEvent(logEvent) || logEvent.Exception is not null))
            .ToList();

    /// <summary>Returns whether the event's <c>EventId</c> is <see cref="ErrorOutcomeMiddleware.UnhandledExceptionEventId"/>.</summary>
    private static bool IsUnhandledExceptionEvent(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue(EventIdProperty, out LogEventPropertyValue? eventId)
        && eventId is StructureValue structure
        && structure.Properties.Any(property =>
            property.Name == EventIdIdProperty
            && Equals(property.Value, new ScalarValue(ErrorOutcomeMiddleware.UnhandledExceptionEventId.Id)));

    /// <summary>Returns whether the event is the HTTP request event of the Serilog request logging.</summary>
    private static bool IsRequestLoggingEvent(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue(SourceContextProperty, out LogEventPropertyValue? sourceContext)
        && Equals(sourceContext, new ScalarValue(RequestLoggingSourceContext));

    /// <summary>Returns the request for the agent <paramref name="name"/> at 314 Nohwere Lane.</summary>
    private static AgentRequest AgentRequestFor(string name) =>
        new(name, new AddressFields(Street, City, CountryName, Phone, Fax, Email, WebSite));

    /// <summary>
    /// Adds the item with id <paramref name="itemId"/> to the session cart of <paramref name="http"/> and asserts that
    /// the add succeeded.
    /// </summary>
    private static async Task AddToCartAsync(HttpClient http, long itemId)
    {
        HttpOutcome outcome = await SendAsync(http, HttpMethod.Post, CartItemsPath, new AddCartItemRequest(itemId, 0m));
        Assert.Equal(HttpStatusCode.OK, outcome.StatusCode);
        CartOperationResponse added = Deserialize<CartOperationResponse>(outcome.Body);
        Assert.True(added.Result.Succeeded, added.Result.FullErrorText);
    }

    /// <summary>
    /// Returns the request for a retail item named <paramref name="name"/> with <paramref name="codeNumber"/>, the
    /// fixture agent, category and region, the price 100.00, no photo and no listed date.
    /// </summary>
    private RetailItemRequest RetailItemRequestFor(string name, int codeNumber) =>
        new(
            codeNumber,
            name,
            ItemDescription,
            ListedDate: null,
            PhotoBase64: null,
            ItemPrice,
            FixtureAgent.Id,
            FixtureCategory.Id,
            FixtureRegion.Id);

    /// <summary>
    /// Creates a tender item named <paramref name="name"/> closing on <paramref name="closureDate"/>, with the reserve
    /// price 5.50, no photo and the fixture agent, category and region.
    /// </summary>
    private Task<TenderItem> CreateTenderItemAsync(string name, DateOnly closureDate) =>
        Builder.CreateTestTenderItemAsync(
            name,
            ItemDescription,
            null,
            FixtureRegion,
            FixtureCategory,
            FixtureAgent,
            TenderReserve,
            closureDate);

    /// <summary>
    /// Opens an administrator session of the admin area on <paramref name="host"/> with <c>POST /api/app/sessions</c>
    /// for the fixture company's name and returns a client of <paramref name="host"/> whose requests carry its id.
    /// </summary>
    private Task<HttpClient> OpenAdministratorSessionAsync(ErewhonApiHost host) =>
        OpenSessionAsync(
            host,
            new CreateSessionRequest(LogonArea.Admin, Administrator: true, FixtureCompany.Name),
            SessionRole.Administrator,
            clientId: null);

    /// <summary>
    /// Opens a shop session for <paramref name="client"/> on <paramref name="host"/> with
    /// <c>POST /api/app/sessions</c> and returns a client of <paramref name="host"/> whose requests carry its id.
    /// </summary>
    private Task<HttpClient> OpenShopSessionAsync(ErewhonApiHost host, Client client) =>
        OpenSessionAsync(
            host,
            new CreateSessionRequest(LogonArea.Shop, Administrator: false, client.Name),
            SessionRole.Client,
            client.Id);

    /// <summary>
    /// Opens a session on <paramref name="host"/> with <paramref name="request"/>, asserts that it succeeded with
    /// <paramref name="role"/> and <paramref name="clientId"/>, records its id for <see cref="TearDownAsync"/> when
    /// <paramref name="host"/> is <see cref="ErewhonTestBase.Api"/>, and returns a client of <paramref name="host"/>
    /// whose requests carry the id in the <see cref="SessionMiddleware.HeaderName"/> header. The caller disposes the
    /// client.
    /// </summary>
    private async Task<HttpClient> OpenSessionAsync(
        ErewhonApiHost host,
        CreateSessionRequest request,
        SessionRole role,
        long? clientId)
    {
        HttpClient http = host.CreateHttpClient();
        try
        {
            HttpOutcome outcome = await SendAsync(http, HttpMethod.Post, SessionsPath, request);
            Assert.Equal(HttpStatusCode.OK, outcome.StatusCode);
            CreateSessionResponse session = Deserialize<CreateSessionResponse>(outcome.Body);
            Assert.True(session.Succeeded, session.Message);
            Assert.Equal(role, session.Role);
            Assert.Equal(clientId, session.ClientId);
            string sessionId = Assert.IsType<string>(session.SessionId);

            if (ReferenceEquals(host, Api))
            {
                _sharedSessionIds.Add(sessionId);
            }

            http.DefaultRequestHeaders.Add(SessionMiddleware.HeaderName, sessionId);
            return http;
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    /// <summary>Returns the id, code number and name of every stored item of the fixture company.</summary>
    private async Task<IReadOnlyList<StoredItem>> ReadItemsAsync()
    {
        long companyId = CompanyId;
        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IErewhonDbContext>()
            .Items
            .AsNoTracking()
            .Where(item => item.CompanyId == companyId)
            .OrderBy(item => item.Id)
            .Select(item => new StoredItem(item.Id, item.CodeNumber, item.Name))
            .ToListAsync();
    }

    /// <summary>Returns the names of the stored agents of the fixture company in ordinal order.</summary>
    private async Task<IReadOnlyList<string>> ReadAgentNamesAsync()
    {
        long companyId = CompanyId;
        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        List<string> names = await scope.ServiceProvider
            .GetRequiredService<IErewhonDbContext>()
            .Agents
            .AsNoTracking()
            .Where(agent => agent.CompanyId == companyId)
            .Select(agent => agent.Name)
            .ToListAsync();
        return names.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Returns the client id of every stored sale of the item with id <paramref name="itemId"/>.</summary>
    private async Task<IReadOnlyList<long>> ReadSaleClientIdsAsync(long itemId)
    {
        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IErewhonDbContext>()
            .Sales
            .AsNoTracking()
            .Where(sale => sale.ItemId == itemId)
            .Select(sale => sale.ClientId)
            .ToListAsync();
    }

    /// <summary>Returns the tender id of every stored tender sale of the item with id <paramref name="itemId"/>.</summary>
    private async Task<IReadOnlyList<long?>> ReadTenderSaleTenderIdsAsync(long itemId)
    {
        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IErewhonDbContext>()
            .Sales
            .OfType<TenderSale>()
            .AsNoTracking()
            .Where(sale => sale.ItemId == itemId)
            .Select(sale => sale.TenderId)
            .ToListAsync();
    }

    /// <summary>Returns the server's <c>deadlock_timeout</c> in milliseconds.</summary>
    private async Task<int> ReadDeadlockTimeoutMsAsync()
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(DeadlockTimeoutSql, connection);
        return Assert.IsType<int>(await command.ExecuteScalarAsync());
    }

    /// <summary>
    /// Returns once a session of the test database waits for the advisory lock <paramref name="lockName"/>, polling
    /// every 20 ms. Throws when <paramref name="request"/> completes first or no session waits within 10 s.
    /// </summary>
    private async Task WaitUntilLockAwaitedAsync(string lockName, Task request)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(WaitingAdvisoryLockSql, connection);
        command.Parameters.AddWithValue("name", lockName);

        var waited = Stopwatch.StartNew();
        while (true)
        {
            if (request.IsCompleted)
            {
                throw new InvalidOperationException($"The request completed before any session waited for {lockName}.");
            }

            if (Assert.IsType<long>(await command.ExecuteScalarAsync()) > 0)
            {
                return;
            }

            if (waited.Elapsed > WaitDeadline)
            {
                throw new TimeoutException($"No session waited for {lockName} within {WaitDeadline}.");
            }

            await Task.Delay(PollInterval);
        }
    }

    /// <summary>The status, media type and body of a response, with the time from sending the request to reading it.</summary>
    private sealed record HttpOutcome(HttpStatusCode StatusCode, string? MediaType, string Body, TimeSpan Elapsed);

    /// <summary>The id, code number and name of a stored item.</summary>
    private sealed record StoredItem(long Id, int CodeNumber, string Name);

    /// <summary>
    /// An unpooled connection to the test database that holds advisory locks and item row locks: session-level locks
    /// outside a transaction, and transaction-level and row locks inside the transaction <see cref="BeginAsync"/> opens.
    /// Disposing it rolls back an open transaction and closes the connection, which ends every lock it holds.
    /// </summary>
    private sealed class LockHolder : IAsyncDisposable
    {
        private const string HoldSessionLockSql = "SELECT pg_advisory_lock(hashtextextended(@name, 0))";
        private const string TryHoldSessionLockSql = "SELECT pg_try_advisory_lock(hashtextextended(@name, 0))";
        private const string ReleaseSessionLockSql = "SELECT pg_advisory_unlock(hashtextextended(@name, 0))";
        private const string HoldXactLockSql = "SELECT pg_advisory_xact_lock(hashtextextended(@name, 0))";
        private const string LockItemRowSql = "SELECT id FROM items WHERE id = @id FOR UPDATE";
        private const string HolderLockTimeoutSql = "SELECT set_config('lock_timeout', '30000ms', true)";

        private readonly NpgsqlConnection _connection;

        private NpgsqlTransaction? _transaction;

        private LockHolder(NpgsqlConnection connection)
        {
            _connection = connection;
        }

        /// <summary>Opens an unpooled connection with the settings of <paramref name="connectionString"/>.</summary>
        public static async Task<LockHolder> OpenAsync(string connectionString)
        {
            var connection = new NpgsqlConnection(
                new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
            try
            {
                await connection.OpenAsync();
                return new LockHolder(connection);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        /// <summary>Takes the session-level advisory lock <paramref name="lockName"/>, waiting until it is granted.</summary>
        public async Task HoldSessionLockAsync(string lockName)
        {
            await using NpgsqlCommand command = NameCommand(HoldSessionLockSql, lockName);
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>Tries the session-level advisory lock <paramref name="lockName"/> without waiting.</summary>
        /// <returns>Whether the lock was obtained.</returns>
        public async Task<bool> TryHoldSessionLockAsync(string lockName)
        {
            await using NpgsqlCommand command = NameCommand(TryHoldSessionLockSql, lockName);
            return Assert.IsType<bool>(await command.ExecuteScalarAsync());
        }

        /// <summary>Releases one hold of the session-level advisory lock <paramref name="lockName"/>.</summary>
        /// <returns>Whether this connection held the lock.</returns>
        public async Task<bool> ReleaseSessionLockAsync(string lockName)
        {
            await using NpgsqlCommand command = NameCommand(ReleaseSessionLockSql, lockName);
            return Assert.IsType<bool>(await command.ExecuteScalarAsync());
        }

        /// <summary>Begins a transaction whose own lock waits end after 30 s.</summary>
        public async Task BeginAsync()
        {
            _transaction = await _connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand(HolderLockTimeoutSql, _connection, _transaction);
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Takes the transaction-level advisory lock <paramref name="lockName"/> in the open transaction, waiting until
        /// it is granted.
        /// </summary>
        public async Task HoldXactLockAsync(string lockName)
        {
            await using NpgsqlCommand command = NameCommand(HoldXactLockSql, lockName);
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Locks the <c>items</c> row with id <paramref name="itemId"/> with <c>FOR UPDATE</c> in the open transaction
        /// and asserts that the row exists.
        /// </summary>
        public async Task LockItemRowAsync(long itemId)
        {
            await using var command = new NpgsqlCommand(LockItemRowSql, _connection, _transaction);
            command.Parameters.AddWithValue("id", itemId);
            Assert.Equal(itemId, await command.ExecuteScalarAsync());
        }

        /// <summary>Rolls back the open transaction, ending its locks; does nothing when none is open.</summary>
        public async Task RollbackAsync()
        {
            if (_transaction is not { } transaction)
            {
                return;
            }

            _transaction = null;
            try
            {
                await transaction.RollbackAsync();
            }
            finally
            {
                await transaction.DisposeAsync();
            }
        }

        /// <summary>Rolls back an open transaction and closes the connection.</summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                await RollbackAsync();
            }
            finally
            {
                await _connection.DisposeAsync();
            }
        }

        /// <summary>Returns a command of <paramref name="sql"/> with the parameter <c>@name</c> = <paramref name="lockName"/>.</summary>
        private NpgsqlCommand NameCommand(string sql, string lockName)
        {
            var command = new NpgsqlCommand(sql, _connection, _transaction);
            command.Parameters.AddWithValue("name", lockName);
            return command;
        }
    }
}
