using System.Collections.Concurrent;
using System.Data.Common;
using Erewhon.Api.Errors;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Cart;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Notifications;
using Erewhon.Application.Services;
using Erewhon.Persistence;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Application.Tests.Cart;

/// <summary>
/// Replays, on PostgreSQL through the production services, <see cref="ShoppingCartService.AddItemAsync"/> racing the
/// committed delete of the item it adds (B-63, B-76). <see cref="AddCommandGate"/> holds the add at a chosen database
/// command while <see cref="ItemService.DeleteByIdAsync(long, CancellationToken)"/> commits and publishes in another
/// scope, and <see cref="RecordingNotificationPublisher"/> records what the delete publishes. Each case builds its own
/// services, bounds every add with <see cref="AddTimeout"/>, and deletes every company it creates.
/// </summary>
public sealed class AddItemDeletionRaceTests : IAsyncLifetime
{
    private const decimal RetailPrice = 40.00m;
    private const decimal TenderReserve = 25.00m;

    private static readonly TimeSpan AddTimeout = TimeSpan.FromSeconds(60);

    private readonly AddCommandGate _gate = new();
    private readonly RecordingNotificationPublisher _publisher = new();
    private ServiceProvider? _services;

    /// <summary>Creates and migrates the test database of the assembly and builds the services over it.</summary>
    /// <returns>A task that completes when the services are ready.</returns>
    public async Task InitializeAsync()
    {
        PostgresDatabase database = PostgresDatabase.ForAssembly(typeof(AddItemDeletionRaceTests).Assembly);
        await database.EnsureCreatedAndMigratedAsync();
        _services = BuildServices(database.ConnectionString, _gate, _publisher);
    }

    /// <summary>Releases a held command, then disposes the services.</summary>
    /// <returns>A task that completes when the services are disposed.</returns>
    public async Task DisposeAsync()
    {
        _gate.Resume();
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }
    }

    /// <summary>
    /// The add is held after its item read has executed, and the item's delete then commits and publishes: the
    /// delete's flush finds no line and sends no <c>CartItemDeleted</c>. Resumed, the add of a retail or a tender line
    /// → <c>$ItemRequired</c>, not succeeded, and the cart holds no line for the item.
    /// </summary>
    /// <param name="tender"><see langword="true"/> for a tender item; <see langword="false"/> for a retail item.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteCommittedAfterItemReadLeavesNoLine(bool tender)
    {
        var companyIds = new List<long>();
        try
        {
            CartFixture fixture = await ArrangeAsync(companyIds);
            long itemId = fixture.ItemId(tender);

            await using AsyncServiceScope add = Services.CreateAsyncScope();
            (ShoppingCartService service, ShoppingCart cart) = OpenCart(add, fixture.ClientId);
            _gate.HoldFirstReaderAfterItExecutes(add.ServiceProvider.GetRequiredService<ErewhonDbContext>());

            OperationResult result = await AddWhileHeldAsync(
                service,
                cart,
                itemId,
                fixture.Offer(tender),
                async () =>
                {
                    await DeleteItemAsync(fixture.CompanyId, itemId);

                    Assert.Contains(
                        _publisher.EntityChanges,
                        change => change.EntityType == ItemType(tender)
                            && change.Id == itemId
                            && change.Kind == ChangeKind.Deleted);
                    Assert.Empty(_publisher.CartItemsDeleted);
                });

            Assert.False(result.Succeeded);
            Assert.Equal(Text(MessageKeys.ItemRequired), Assert.Single(result.Errors));
            Assert.False(cart.Contains(itemId));
            Assert.Empty(_publisher.CartItemsDeleted);
        }
        finally
        {
            await DeleteCompaniesAsync(companyIds);
        }
    }

    /// <summary>
    /// The add is held at the existence read that follows its insert, with its retail or tender line in the cart, and
    /// the item's delete then commits and publishes: the delete's flush removes the line and sends one
    /// <c>CartItemDeleted</c> to the session. Resumed, the add → <c>$ItemRequired</c>, not succeeded, and the cart
    /// holds no line for the item.
    /// </summary>
    /// <param name="tender"><see langword="true"/> for a tender item; <see langword="false"/> for a retail item.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteCommittedAtExistenceReadEvictsLineAndNotifies(bool tender)
    {
        var companyIds = new List<long>();
        try
        {
            CartFixture fixture = await ArrangeAsync(companyIds);
            long itemId = fixture.ItemId(tender);

            await using AsyncServiceScope add = Services.CreateAsyncScope();
            (ShoppingCartService service, ShoppingCart cart) = OpenCart(add, fixture.ClientId);
            _gate.HoldFirstReaderBeforeItExecutesWhen(
                add.ServiceProvider.GetRequiredService<ErewhonDbContext>(),
                () => cart.Contains(itemId));

            OperationResult result = await AddWhileHeldAsync(
                service,
                cart,
                itemId,
                fixture.Offer(tender),
                async () =>
                {
                    Assert.True(cart.Contains(itemId));

                    await DeleteItemAsync(fixture.CompanyId, itemId);

                    Assert.False(cart.Contains(itemId));
                    Assert.Equal((cart.SessionId, itemId), Assert.Single(_publisher.CartItemsDeleted));
                });

            Assert.False(result.Succeeded);
            Assert.Equal(Text(MessageKeys.ItemRequired), Assert.Single(result.Errors));
            Assert.False(cart.Contains(itemId));
            Assert.Equal((cart.SessionId, itemId), Assert.Single(_publisher.CartItemsDeleted));
        }
        finally
        {
            await DeleteCompaniesAsync(companyIds);
        }
    }

    /// <summary>
    /// With no delete, the add of a retail line → succeeded, and the cart keeps the line at the item's price; the add of
    /// a tender line → succeeded, and the cart keeps the line with the offer and a time tendered. No
    /// <c>CartItemDeleted</c> is sent.
    /// </summary>
    /// <param name="tender"><see langword="true"/> for a tender item; <see langword="false"/> for a retail item.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddWithoutDeleteKeepsLine(bool tender)
    {
        var companyIds = new List<long>();
        try
        {
            CartFixture fixture = await ArrangeAsync(companyIds);
            long itemId = fixture.ItemId(tender);

            await using AsyncServiceScope add = Services.CreateAsyncScope();
            (ShoppingCartService service, ShoppingCart cart) = OpenCart(add, fixture.ClientId);
            using var timeout = new CancellationTokenSource(AddTimeout);

            OperationResult result = await service.AddItemAsync(cart.SessionId, itemId, fixture.Offer(tender), timeout.Token);

            Assert.True(result.Succeeded, result.FullErrorText);
            Assert.Empty(result.Errors);
            CartItem line = Assert.Single(cart.Items);
            Assert.Equal(itemId, line.ItemId);
            Assert.Equal(tender, line.IsTender);
            Assert.Equal(fixture.ClientId, line.ClientId);
            if (tender)
            {
                Assert.Equal(TenderReserve, line.Offer);
                Assert.NotNull(line.DateTendered);
            }
            else
            {
                Assert.Equal(RetailPrice, line.SalePrice);
                Assert.Null(line.DateTendered);
            }

            Assert.Empty(_publisher.CartItemsDeleted);
        }
        finally
        {
            await DeleteCompaniesAsync(companyIds);
        }
    }

    /// <summary>
    /// A second add of an item whose retail or tender line the cart already holds → <see cref="KeyAlreadyUsedException"/>
    /// 1310 "Key already used in this dictionary", whose error outcome is headed "Error 1310" with the web exception
    /// body, and the cart still holds the first line object, unchanged.
    /// </summary>
    /// <param name="tender"><see langword="true"/> for a tender item; <see langword="false"/> for a retail item.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateAddThrowsAndKeepsFirstLine(bool tender)
    {
        var companyIds = new List<long>();
        try
        {
            CartFixture fixture = await ArrangeAsync(companyIds);
            long itemId = fixture.ItemId(tender);

            await using AsyncServiceScope add = Services.CreateAsyncScope();
            (ShoppingCartService service, ShoppingCart cart) = OpenCart(add, fixture.ClientId);
            using var timeout = new CancellationTokenSource(AddTimeout);

            OperationResult first = await service.AddItemAsync(cart.SessionId, itemId, fixture.Offer(tender), timeout.Token);
            Assert.True(first.Succeeded, first.FullErrorText);
            CartItem firstLine = Assert.Single(cart.Items);

            KeyAlreadyUsedException failure = await Assert.ThrowsAsync<KeyAlreadyUsedException>(
                () => service.AddItemAsync(cart.SessionId, itemId, fixture.Offer(tender), timeout.Token));

            Assert.Equal(KeyAlreadyUsedException.Code, failure.ErrorCode);
            Assert.Equal("Key already used in this dictionary", failure.Message);
            ErrorOutcome outcome = ErrorOutcomeMiddleware.Create(failure);
            Assert.Equal(ErrorOutcome.KindException, outcome.Kind);
            Assert.Equal("Error 1310", outcome.Heading);
            Assert.Equal(
                MessageCatalog.Get(
                    MessageKeys.BaseExceptionBodyWeb,
                    "1310",
                    "Key already used in this dictionary",
                    MessageCatalog.Get(MessageKeys.BaseNotAvailable),
                    string.Empty),
                outcome.Body);
            Assert.Same(firstLine, Assert.Single(cart.Items));
        }
        finally
        {
            await DeleteCompaniesAsync(companyIds);
        }
    }

    /// <summary>
    /// The add is held at the existence read that follows its insert, and its token is then cancelled: the add →
    /// <see cref="OperationCanceledException"/>, the cart holds no line for the item, and the item still exists.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task CancelledExistenceReadRemovesLineAndPropagates()
    {
        var companyIds = new List<long>();
        try
        {
            CartFixture fixture = await ArrangeAsync(companyIds);
            long itemId = fixture.RetailItemId;

            await using AsyncServiceScope add = Services.CreateAsyncScope();
            (ShoppingCartService service, ShoppingCart cart) = OpenCart(add, fixture.ClientId);
            _gate.HoldFirstReaderBeforeItExecutesWhen(
                add.ServiceProvider.GetRequiredService<ErewhonDbContext>(),
                () => cart.Contains(itemId));
            using var cancellation = new CancellationTokenSource(AddTimeout);

            Task<OperationResult> adding = service.AddItemAsync(cart.SessionId, itemId, 0m, cancellation.Token);
            Assert.Same(_gate.Held, await Task.WhenAny(_gate.Held, adding));
            Assert.True(cart.Contains(itemId));

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adding);
            Assert.False(cart.Contains(itemId));
            await using AsyncServiceScope assert = Services.CreateAsyncScope();
            Assert.True(await assert.ServiceProvider.GetRequiredService<ItemService>().ExistsAsync(itemId));
        }
        finally
        {
            await DeleteCompaniesAsync(companyIds);
        }
    }

    /// <summary>The services built by <see cref="InitializeAsync"/>.</summary>
    private ServiceProvider Services =>
        _services ?? throw new InvalidOperationException("The services are built by InitializeAsync.");

    /// <summary>
    /// Returns the production application and persistence services over the database at
    /// <paramref name="connectionString"/>: one database context, unit of work, lock and clock adapter, notification
    /// queue, bound company and <see cref="TestSessionContext"/> per scope, the session carts shared by every scope,
    /// notifications handed to <paramref name="publisher"/>, and <paramref name="gate"/> intercepting every context's
    /// commands.
    /// </summary>
    private static ServiceProvider BuildServices(
        string connectionString,
        AddCommandGate gate,
        RecordingNotificationPublisher publisher)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICartStore, InMemoryCartStore>();
        services.AddSingleton<INotificationPublisher>(publisher);
        services.AddSingleton(new EntityLockSettings());
        services.AddSingleton<PostgresErrorMapper>();
        services.AddScoped<NotificationQueue>();
        services.AddScoped<NotificationDeriver>();
        services.AddScoped<ChangeNotificationInterceptor>();
        services.AddScoped(provider => new ErewhonDbContext(
            new DbContextOptionsBuilder<ErewhonDbContext>()
                .UseNpgsql(connectionString)
                .AddInterceptors(gate)
                .Options,
            provider.GetRequiredService<ChangeNotificationInterceptor>()));
        services.AddScoped<IErewhonDbContext>(provider => provider.GetRequiredService<ErewhonDbContext>());
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IEntityLocks, PostgresEntityLocks>();
        services.AddScoped<IServerClock, PostgresServerClock>();
        services.AddScoped<ICompanyContext, CompanyContext>();
        services.AddScoped<TestSessionContext>();
        services.AddScoped<ISessionContext>(provider => provider.GetRequiredService<TestSessionContext>());
        services.AddScoped<ItemCodeAllocator>();
        services.AddScoped<CompanyService>();
        services.AddScoped<AgentService>();
        services.AddScoped<ClientService>();
        services.AddScoped<CommissionRateService>();
        services.AddScoped<ItemCategoryService>();
        services.AddScoped<CountryService>();
        services.AddScoped<RegionService>();
        services.AddScoped<ItemService>();
        services.AddScoped<RetailSaleService>();
        services.AddScoped<TenderSaleService>();
        services.AddScoped<TenderService>();
        services.AddScoped<ShoppingCartService>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>Returns the text of the message with key <paramref name="key"/>.</summary>
    private static string Text(string key) => MessageCatalog.Get(key);

    /// <summary>Returns the entity type name of the item's change events.</summary>
    private static string ItemType(bool tender) => tender ? nameof(TenderItem) : nameof(RetailItem);

    /// <summary>
    /// Returns the cart service of <paramref name="scope"/>, whose session client is set to
    /// <paramref name="clientId"/>, and the cart of that scope's session.
    /// </summary>
    private static (ShoppingCartService Service, ShoppingCart Cart) OpenCart(AsyncServiceScope scope, long clientId)
    {
        TestSessionContext session = scope.ServiceProvider.GetRequiredService<TestSessionContext>();
        session.SetClient(clientId);
        string sessionId = session.SessionId
            ?? throw new InvalidOperationException("A test session always has an id.");
        return (
            scope.ServiceProvider.GetRequiredService<ShoppingCartService>(),
            scope.ServiceProvider.GetRequiredService<ICartStore>().GetCart(sessionId));
    }

    /// <summary>
    /// Starts the add of the item to <paramref name="cart"/>, bounded by <see cref="AddTimeout"/>; asserts that the
    /// add reached the command <see cref="_gate"/> holds before it completed; runs <paramref name="whileHeld"/>; then
    /// releases the command and returns the add's result. When a step before the release fails, releases the command
    /// and waits for the add to end before the failure propagates.
    /// </summary>
    private async Task<OperationResult> AddWhileHeldAsync(
        ShoppingCartService service,
        ShoppingCart cart,
        long itemId,
        decimal offer,
        Func<Task> whileHeld)
    {
        using var timeout = new CancellationTokenSource(AddTimeout);
        Task<OperationResult> adding = service.AddItemAsync(cart.SessionId, itemId, offer, timeout.Token);
        try
        {
            Assert.Same(_gate.Held, await Task.WhenAny(_gate.Held, adding));
            await whileHeld();
        }
        catch
        {
            _gate.Resume();
            await Task.WhenAny(adding);
            throw;
        }

        _gate.Resume();
        return await adding;
    }

    /// <summary>
    /// Deletes the item with row id <paramref name="itemId"/> through <see cref="ItemService"/> in its own transaction,
    /// in a new scope with the company bound; asserts that the delete succeeds. The delete has committed and published
    /// when the returned task completes.
    /// </summary>
    private async Task DeleteItemAsync(long companyId, long itemId)
    {
        await using AsyncServiceScope delete = Services.CreateAsyncScope();
        delete.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(companyId);
        OperationResult result = await delete.ServiceProvider.GetRequiredService<ItemService>()
            .DeleteByIdAsync(itemId, CancellationToken.None);
        Assert.True(result.Succeeded, result.FullErrorText);
    }

    /// <summary>
    /// Creates, in a new scope, a company with an agent, a client, a region and a category, a retail item of that
    /// category priced <see cref="RetailPrice"/> and a tender item with reserve <see cref="TenderReserve"/> closing in
    /// five days; adds the company's id to <paramref name="companyIds"/>.
    /// </summary>
    private async Task<CartFixture> ArrangeAsync(List<long> companyIds)
    {
        await using AsyncServiceScope arrange = Services.CreateAsyncScope();
        var builder = new TestCompanyBuilder(arrange.ServiceProvider);
        Company shop = await builder.CreateCompanyAsync();
        companyIds.Add(shop.Id);
        builder.Bind(shop.Id);

        Agent agent = await builder.CreateAgentAsync();
        Client client = await builder.CreateClientAsync();
        Region region = await builder.CreateRegionAsync(await builder.CreateCountryAsync());
        ItemCategory category = await builder.CreateItemCategoryAsync("Widgets", "WIDG", "Widgets");
        RetailItem retailItem = await builder.CreateTestRetailItemAsync(
            "Raced Retail", "Deleted while added", null, region, category, agent, RetailPrice);
        DateOnly today = await arrange.ServiceProvider.GetRequiredService<IServerClock>().GetServerDateAsync();
        TenderItem tenderItem = await builder.CreateTestTenderItemAsync(
            "Raced Tender", "Deleted while added", null, region, category, agent, TenderReserve, today.AddDays(5));
        return new CartFixture(shop.Id, client.Id, retailItem.Id, tenderItem.Id);
    }

    /// <summary>Deletes, in a new scope and in reverse order of creation, every company in <paramref name="companyIds"/>.</summary>
    private async Task DeleteCompaniesAsync(List<long> companyIds)
    {
        await using AsyncServiceScope cleanup = Services.CreateAsyncScope();
        var builder = new TestCompanyBuilder(cleanup.ServiceProvider);
        for (int index = companyIds.Count - 1; index >= 0; index--)
        {
            await builder.DeleteCompanyAsync(companyIds[index]);
        }
    }

    /// <summary>The row ids of one case's company, client, retail item and tender item.</summary>
    private sealed record CartFixture(long CompanyId, long ClientId, long RetailItemId, long TenderItemId)
    {
        /// <summary>Returns the row id of the tender item or of the retail item.</summary>
        public long ItemId(bool tender) => tender ? TenderItemId : RetailItemId;

        /// <summary>Returns the offer of the add: the tender item's reserve, or 0 for the retail item.</summary>
        public decimal Offer(bool tender) => tender ? TenderReserve : 0m;
    }

    /// <summary>
    /// Holds one reader command of one database context until <see cref="Resume"/> is called, or until the command's
    /// token is cancelled: either the context's first reader command once it has executed, or the context's first
    /// reader command that starts while a condition holds, before it executes. <see cref="Held"/> completes when the
    /// command is held. Commands of other contexts, and every command after the held one, run unheld.
    /// </summary>
    private sealed class AddCommandGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile DbContext? _target;
        private volatile Func<bool>? _beforeExecutingWhen;
        private int _holding;

        /// <summary>A task that completes when a command is held.</summary>
        public Task Held => _held.Task;

        /// <summary>Holds the first reader command of <paramref name="target"/> once it has executed.</summary>
        public void HoldFirstReaderAfterItExecutes(DbContext target)
        {
            _beforeExecutingWhen = null;
            _target = target;
        }

        /// <summary>
        /// Holds, before it executes, the first reader command of <paramref name="target"/> that starts while
        /// <paramref name="condition"/> returns <see langword="true"/>.
        /// </summary>
        public void HoldFirstReaderBeforeItExecutesWhen(DbContext target, Func<bool> condition)
        {
            _beforeExecutingWhen = condition;
            _target = target;
        }

        /// <summary>Releases the held command, and every command held later, at once.</summary>
        public void Resume() => _resumed.TrySetResult();

        /// <inheritdoc />
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (IsTarget(eventData.Context) && _beforeExecutingWhen is { } condition && condition() && TryHold())
            {
                await _resumed.Task.WaitAsync(cancellationToken);
            }

            return result;
        }

        /// <inheritdoc />
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (IsTarget(eventData.Context) && _beforeExecutingWhen is null && TryHold())
            {
                await _resumed.Task.WaitAsync(cancellationToken);
            }

            return result;
        }

        /// <summary>Returns <see langword="true"/> when <paramref name="context"/> is the armed context.</summary>
        private bool IsTarget(DbContext? context) => context is not null && ReferenceEquals(context, _target);

        /// <summary>
        /// Returns <see langword="true"/>, and completes <see cref="Held"/>, for the first command that reaches it.
        /// </summary>
        private bool TryHold()
        {
            if (Interlocked.CompareExchange(ref _holding, 1, 0) != 0)
            {
                return false;
            }

            _held.TrySetResult();
            return true;
        }
    }

    /// <summary>Records every entity change and every <c>CartItemDeleted</c> it is given, in the order given.</summary>
    private sealed class RecordingNotificationPublisher : INotificationPublisher
    {
        private readonly ConcurrentQueue<ChangeEvent> _entityChanges = new();
        private readonly ConcurrentQueue<(string SessionId, long ItemId)> _cartItemsDeleted = new();

        /// <summary>A snapshot of the entity changes published so far.</summary>
        public IReadOnlyList<ChangeEvent> EntityChanges => _entityChanges.ToArray();

        /// <summary>A snapshot of the session and item of each <c>CartItemDeleted</c> published so far.</summary>
        public IReadOnlyList<(string SessionId, long ItemId)> CartItemsDeleted => _cartItemsDeleted.ToArray();

        /// <inheritdoc />
        public Task PublishEntityChangesAsync(IReadOnlyList<ChangeEvent> events, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(events);
            foreach (ChangeEvent change in events)
            {
                _entityChanges.Enqueue(change);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task PublishCartItemDeletedAsync(string sessionId, long itemId, CancellationToken ct = default)
        {
            _cartItemsDeleted.Enqueue((sessionId, itemId));
            return Task.CompletedTask;
        }

        /// <summary>Always throws: no case of these tests requests a save.</summary>
        /// <exception cref="InvalidOperationException">Always.</exception>
        public Task PublishSaveOutstandingChangesAsync(
            string sessionId,
            string entityType,
            long id,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("No case of these tests requests a save.");
    }
}
