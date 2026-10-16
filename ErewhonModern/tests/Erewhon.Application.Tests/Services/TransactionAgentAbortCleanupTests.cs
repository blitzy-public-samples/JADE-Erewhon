using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erewhon.Application.Tests.Services;

/// <summary>
/// The abort cleanup of the transaction-agent template after a save that PostgreSQL rejects, run through the production
/// services on the PostgreSQL test database against the fixture company of <see cref="ErewhonTestBase"/>. The company
/// holds an agent, a client, a region, an item category, a tender item closing on the server date with one tender by the
/// client, and a retail item. A CHECK constraint on <c>sales</c> that rejects one sale type, added and committed on a
/// connection of its own before the operation and dropped after it, makes the sale's insert fail with SQLSTATE
/// <c>23514</c>. Each operation runs in a new scope with the company bound.
/// </summary>
public sealed class TransactionAgentAbortCleanupTests : ErewhonTestBase
{
    private const string RejectingConstraint = "ck_sales_rejected_by_abort_cleanup_tests";

    private const string CallerDescription = "Changed by the caller after the failed sale";

    private long _agentId;

    private long _clientId;

    private long _tenderItemId;

    private long _tenderId;

    private long _retailItemId;

    private long CompanyId => Builder.MyCompany?.Id
        ?? throw new InvalidOperationException("The fixture company has not been created.");

    /// <summary>
    /// A tender closure at the server date, run in one scope, whose Ambient tender-sale save PostgreSQL rejects, raises
    /// the <see cref="DbUpdateException"/> of that save, holding the <see cref="PostgresException"/> with SQLSTATE
    /// <c>23514</c> of the rejecting constraint, and no <see cref="InvalidOperationException"/>; nothing is awarded.
    /// Once the constraint is dropped, a second closure at the same date in the same scope awards the tender item and
    /// returns 1, and the item then has one tender sale, on the client's tender, to the client.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ClosureWhoseTenderSaleSaveFailsRaisesTheDatabaseFailureAndTheSameScopeClosesAgain()
    {
        await using AsyncServiceScope scope = NewBoundScope();
        TenderClosureService closure = scope.ServiceProvider.GetRequiredService<TenderClosureService>();
        DateOnly date = await scope.ServiceProvider.GetRequiredService<IServerClock>().GetServerDateAsync();

        await AddRejectingConstraintAsync(tenderSale: true);
        try
        {
            Exception? failure = await Record.ExceptionAsync(() => closure.CloseTendersAtDateAsync(date));

            AssertCheckViolation(failure);
            Assert.False(await SaleExistsAsync(_tenderItemId));
        }
        finally
        {
            await DropRejectingConstraintAsync();
        }

        int closed = await closure.CloseTendersAtDateAsync(date);

        Assert.Equal(1, closed);
        await using AsyncServiceScope read = Api.Services.CreateAsyncScope();
        var sale = Assert.Single(await read.ServiceProvider.GetRequiredService<IErewhonDbContext>().Sales
            .AsNoTracking()
            .OfType<TenderSale>()
            .Where(s => s.ItemId == _tenderItemId)
            .Select(s => new { s.TenderId, s.ClientId })
            .ToListAsync());
        Assert.Equal(new { TenderId = (long?)_tenderId, ClientId = _clientId }, sale);
    }

    /// <summary>
    /// A retail or tender sale whose insert PostgreSQL rejects, run in <see cref="TransactionMode.Own"/> mode or in
    /// <see cref="TransactionMode.Ambient"/> mode inside a transaction the caller began, while the scope tracks the
    /// bound company, the item and, for a tender sale, the client's tender, whose <see cref="Tender.TenderItem"/> holds
    /// the item: the operation raises the <see cref="DbUpdateException"/> of the save, holding the
    /// <see cref="PostgresException"/> with SQLSTATE <c>23514</c> of the rejecting constraint. Afterwards the command's
    /// model object is <see langword="null"/>; the change tracker holds no added entity and exactly the entities it held
    /// before, each in its entity state, with every navigation holding the entity or entities and the loaded state it
    /// had before: <see cref="Item.Sale"/> and <see cref="Tender.TenderSale"/> <see langword="null"/>,
    /// <see cref="Tender.TenderItem"/> the item; the tender's foreign key names the item, and change detection finds no
    /// change. In Ambient mode the caller's transaction then saves a change of the tracked item's description and
    /// commits. No sale of the item exists, and in Ambient mode the item holds the caller's description.
    /// </summary>
    /// <param name="tenderSale"><see langword="true"/> for a tender sale; <see langword="false"/> for a retail sale.</param>
    /// <param name="mode">The transaction mode of the sale.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData(false, TransactionMode.Own)]
    [InlineData(true, TransactionMode.Own)]
    [InlineData(false, TransactionMode.Ambient)]
    [InlineData(true, TransactionMode.Ambient)]
    public async Task SaleWhoseSaveFailsRaisesTheDatabaseFailureAndRestoresTheTrackedEntities(
        bool tenderSale,
        TransactionMode mode)
    {
        long itemId = tenderSale ? _tenderItemId : _retailItemId;
        string? expectedDescription = null;

        await AddRejectingConstraintAsync(tenderSale);
        try
        {
            await using AsyncServiceScope scope = NewBoundScope();
            IErewhonDbContext db = scope.ServiceProvider.GetRequiredService<IErewhonDbContext>();
            IUnitOfWork uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await scope.ServiceProvider.GetRequiredService<ICompanyContext>().GetCurrentAsync();
            Item item = await db.Items.SingleAsync(i => i.Id == itemId);
            expectedDescription = mode == TransactionMode.Ambient ? CallerDescription : item.Description;
            Tender? tender = null;
            if (tenderSale)
            {
                tender = await db.Tenders.SingleAsync(t => t.Id == _tenderId);
                Assert.Same(item, tender.TenderItem);
            }

            Dictionary<object, TrackedState> trackedBefore = TrackedStates(db);

            if (mode == TransactionMode.Ambient)
            {
                await uow.BeginAsync();
            }

            TransactionCommand<Sale> command = tenderSale
                ? new TenderSaleCommand { ItemId = itemId, TenderId = _tenderId, ClientId = _clientId }
                : new RetailSaleCommand { ItemId = itemId, ClientId = _clientId, SalePrice = 40m };
            TransactionAgent<Sale> service = tenderSale
                ? scope.ServiceProvider.GetRequiredService<TenderSaleService>()
                : scope.ServiceProvider.GetRequiredService<RetailSaleService>();

            Exception? failure = await Record.ExceptionAsync(
                () => service.ExecuteAsync(command, TransactionType.Persist, mode));

            AssertCheckViolation(failure);
            Assert.Null(command.ModelObject);
            Assert.DoesNotContain(db.ChangeTracker.Entries(), entry => entry.State == EntityState.Added);
            AssertTrackedAsBefore(trackedBefore, TrackedStates(db));
            Assert.Null(item.Sale);
            if (tender is not null)
            {
                Assert.Null(tender.TenderSale);
                Assert.Same(item, tender.TenderItem);
                Assert.Equal(itemId, tender.TenderItemId);
            }

            db.ChangeTracker.DetectChanges();
            Assert.False(db.ChangeTracker.HasChanges());

            if (mode == TransactionMode.Ambient)
            {
                Assert.True(uow.HasActiveTransaction);
                item.Description = CallerDescription;
                await db.SaveChangesAsync();
                await uow.CommitAsync();
            }
        }
        finally
        {
            await DropRejectingConstraintAsync();
        }

        Assert.False(await SaleExistsAsync(itemId));
        await using AsyncServiceScope read = Api.Services.CreateAsyncScope();
        string description = await read.ServiceProvider.GetRequiredService<IErewhonDbContext>().Items
            .AsNoTracking()
            .Where(i => i.Id == itemId)
            .Select(i => i.Description)
            .SingleAsync();
        Assert.Equal(expectedDescription, description);
    }

    /// <summary>
    /// A retail sale whose insert PostgreSQL rejects, run in <see cref="TransactionMode.Own"/> mode while the scope
    /// tracks the item's agent, which the caller attached as unchanged with its owned address and without the other
    /// entities it references, after a read without tracking that included the agent's company, and then the bound
    /// company: <see cref="Agent.Company"/> holds the untracked company and is not loaded until the sale's commission
    /// read loads it. The operation raises the <see cref="DbUpdateException"/> of the save, holding the
    /// <see cref="PostgresException"/> with SQLSTATE <c>23514</c> of the rejecting constraint. Afterwards the change
    /// tracker holds no added entity and exactly the entities it held before, each in its entity state and with the
    /// navigation contents and loaded states it had before; <see cref="Agent.Company"/> holds the untracked company,
    /// which is still untracked, and is not loaded; the agent's company id is unchanged, and change detection finds no
    /// change.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SaleWhoseSaveFailsRestoresANotLoadedReferenceHoldingAnUntrackedEntity()
    {
        await AddRejectingConstraintAsync(tenderSale: false);
        try
        {
            await using AsyncServiceScope scope = NewBoundScope();
            IErewhonDbContext db = scope.ServiceProvider.GetRequiredService<IErewhonDbContext>();

            Agent agent = await db.Agents
                .AsNoTracking()
                .Include(a => a.Company)
                .SingleAsync(a => a.Id == _agentId);
            Company heldCompany = Assert.IsType<Company>(agent.Company);
            db.ChangeTracker.TrackGraph(agent, node =>
            {
                if (node.SourceEntry is null || node.Entry.Metadata.IsOwned())
                {
                    node.Entry.State = EntityState.Unchanged;
                }
            });
            await scope.ServiceProvider.GetRequiredService<ICompanyContext>().GetCurrentAsync();
            ReferenceEntry<Agent, Company> companyReference = db.Entry(agent).Reference(a => a.Company);
            Assert.Same(heldCompany, agent.Company);
            Assert.False(companyReference.IsLoaded);
            Assert.Equal(EntityState.Detached, db.Entry(heldCompany).State);

            Dictionary<object, TrackedState> trackedBefore = TrackedStates(db);
            var command = new RetailSaleCommand { ItemId = _retailItemId, ClientId = _clientId, SalePrice = 40m };

            Exception? failure = await Record.ExceptionAsync(
                () => scope.ServiceProvider.GetRequiredService<RetailSaleService>()
                    .ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own));

            AssertCheckViolation(failure);
            Assert.Null(command.ModelObject);
            Assert.DoesNotContain(db.ChangeTracker.Entries(), entry => entry.State == EntityState.Added);
            AssertTrackedAsBefore(trackedBefore, TrackedStates(db));
            Assert.Same(heldCompany, agent.Company);
            Assert.False(companyReference.IsLoaded);
            Assert.Equal(EntityState.Detached, db.Entry(heldCompany).State);
            Assert.Equal(CompanyId, agent.CompanyId);

            db.ChangeTracker.DetectChanges();
            Assert.False(db.ChangeTracker.HasChanges());
            Assert.Equal(EntityState.Unchanged, db.Entry(agent).State);
            Assert.Same(heldCompany, agent.Company);
        }
        finally
        {
            await DropRejectingConstraintAsync();
        }

        Assert.False(await SaleExistsAsync(_retailItemId));
    }

    /// <summary>
    /// Creates and binds the company, then creates, in a scope of its own, the agent, the client, the country, the
    /// region, the item category "Test Category" ("TEST"), the tender item closing on the server date, a tender by the
    /// client offering the item's reserve plus 10, and the retail item.
    /// </summary>
    /// <returns>A task that completes when the fixtures exist.</returns>
    protected override async Task SetUpAsync()
    {
        await base.SetUpAsync();

        await using AsyncServiceScope arrange = NewBoundScope();
        var builder = new TestCompanyBuilder(arrange.ServiceProvider);
        Agent agent = await builder.CreateAgentAsync();
        Client client = await builder.CreateClientAsync();
        Region region = await builder.CreateRegionAsync(await builder.CreateCountryAsync());
        ItemCategory category = await builder.CreateItemCategoryAsync("Test Category", "TEST", "Test Category Description");
        TenderItem tenderItem = await builder.CreateTenderItemAsync(region, category, agent);
        Tender tender = await builder.CreateTenderAsync(client, tenderItem, tenderItem.Price + 10);
        RetailItem retailItem = await builder.CreateRetailItemAsync(region, category, agent);

        _agentId = agent.Id;
        _clientId = client.Id;
        _tenderItemId = tenderItem.Id;
        _tenderId = tender.Id;
        _retailItemId = retailItem.Id;
    }

    /// <summary>
    /// Asserts that <paramref name="failure"/> is a <see cref="DbUpdateException"/> whose chain of inner exceptions
    /// holds a <see cref="PostgresException"/> with SQLSTATE <c>23514</c> raised by the rejecting constraint.
    /// </summary>
    private static void AssertCheckViolation(Exception? failure)
    {
        Assert.NotNull(failure);
        Assert.IsNotType<InvalidOperationException>(failure);
        DbUpdateException update = Assert.IsType<DbUpdateException>(failure);

        PostgresException? postgres = null;
        for (Exception? inner = update.InnerException; inner is not null && postgres is null; inner = inner.InnerException)
        {
            postgres = inner as PostgresException;
        }

        Assert.NotNull(postgres);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
        Assert.Equal(RejectingConstraint, postgres.ConstraintName);
    }

    /// <summary>
    /// Returns, for each entity the change tracker of <paramref name="db"/> holds, its entity state and, by navigation
    /// name, the entity or the entities in order each navigation holds and whether it is loaded.
    /// </summary>
    private static Dictionary<object, TrackedState> TrackedStates(IErewhonDbContext db)
    {
        var states = new Dictionary<object, TrackedState>(ReferenceEqualityComparer.Instance);
        foreach (EntityEntry entry in db.ChangeTracker.Entries())
        {
            var navigations = new Dictionary<string, NavigationState>(StringComparer.Ordinal);
            foreach (NavigationEntry navigation in entry.Navigations)
            {
                object? value = navigation.CurrentValue;
                object[] targets = navigation.Metadata.IsCollection
                    ? (value as System.Collections.IEnumerable)?.Cast<object>().ToArray() ?? []
                    : value is null ? [] : [value];
                navigations.Add(navigation.Metadata.Name, new NavigationState(targets, navigation.IsLoaded));
            }

            states.Add(entry.Entity, new TrackedState(entry.Metadata.DisplayName(), entry.State, navigations));
        }

        return states;
    }

    /// <summary>
    /// Asserts that <paramref name="after"/> holds exactly the entities of <paramref name="before"/>, each in the same
    /// entity state, with each navigation holding the same entities, compared by reference and in order, and having
    /// the same loaded state.
    /// </summary>
    private static void AssertTrackedAsBefore(
        Dictionary<object, TrackedState> before,
        Dictionary<object, TrackedState> after)
    {
        Assert.Empty(after
            .Where(tracked => !before.ContainsKey(tracked.Key))
            .Select(tracked => $"{tracked.Value.EntityType} {tracked.Value.State}"));
        foreach ((object entity, TrackedState expected) in before)
        {
            Assert.True(
                after.TryGetValue(entity, out TrackedState? actual),
                $"A tracked {expected.EntityType} is no longer tracked.");
            Assert.Equal(expected.State, actual.State);
            foreach ((string name, NavigationState navigation) in expected.Navigations)
            {
                NavigationState restored = actual.Navigations[name];
                Assert.True(
                    navigation.IsLoaded == restored.IsLoaded,
                    $"{expected.EntityType}.{name} has the loaded state {restored.IsLoaded}, not {navigation.IsLoaded}.");
                Assert.True(
                    navigation.Targets.Length == restored.Targets.Length
                        && navigation.Targets.Zip(restored.Targets).All(pair => ReferenceEquals(pair.First, pair.Second)),
                    $"{expected.EntityType}.{name} holds [{Describe(restored.Targets)}], not [{Describe(navigation.Targets)}].");
            }
        }
    }

    /// <summary>
    /// Returns the CLR type name of each of <paramref name="entities"/>, with its row id when it is a
    /// <see cref="PersistentModel"/>, separated by commas.
    /// </summary>
    private static string Describe(object[] entities) =>
        string.Join(", ", entities.Select(entity => entity is PersistentModel model
            ? $"{entity.GetType().Name} {model.Id}"
            : entity.GetType().Name));

    /// <summary>Opens a new scope of the API with the fixture company bound.</summary>
    private AsyncServiceScope NewBoundScope()
    {
        AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(CompanyId);
        return scope;
    }

    /// <summary>Returns whether a sale of the item with row id <paramref name="itemId"/> exists, read in a new scope.</summary>
    private async Task<bool> SaleExistsAsync(long itemId)
    {
        await using AsyncServiceScope read = Api.Services.CreateAsyncScope();
        return await read.ServiceProvider.GetRequiredService<IErewhonDbContext>().Sales
            .AsNoTracking()
            .AnyAsync(sale => sale.ItemId == itemId);
    }

    /// <summary>
    /// Adds and commits, on a connection of its own, the CHECK constraint <see cref="RejectingConstraint"/> on
    /// <c>sales</c>, not validated against existing rows, which rejects every tender sale when
    /// <paramref name="tenderSale"/> is <see langword="true"/> and every retail sale otherwise.
    /// </summary>
    private Task AddRejectingConstraintAsync(bool tenderSale) =>
        ExecuteOnOwnConnectionAsync(tenderSale
            ? $"ALTER TABLE sales ADD CONSTRAINT {RejectingConstraint} CHECK (sale_type <> 'T') NOT VALID"
            : $"ALTER TABLE sales ADD CONSTRAINT {RejectingConstraint} CHECK (sale_type <> 'R') NOT VALID");

    /// <summary>Drops and commits, on a connection of its own, <see cref="RejectingConstraint"/> when it exists.</summary>
    private Task DropRejectingConstraintAsync() =>
        ExecuteOnOwnConnectionAsync($"ALTER TABLE sales DROP CONSTRAINT IF EXISTS {RejectingConstraint}");

    /// <summary>Runs <paramref name="sql"/> on a new connection to the test database, outside any transaction.</summary>
    private async Task ExecuteOnOwnConnectionAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The entity state of a tracked entity and the state of each of its navigations, by navigation name.</summary>
    private sealed record TrackedState(string EntityType, EntityState State, Dictionary<string, NavigationState> Navigations);

    /// <summary>The entities a navigation holds, in order, and whether it is loaded.</summary>
    private sealed record NavigationState(object[] Targets, bool IsLoaded);
}
