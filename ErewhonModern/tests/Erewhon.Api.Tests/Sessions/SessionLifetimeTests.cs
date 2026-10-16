using System.Net;
using Erewhon.Api.Closure;
using Erewhon.Api.Sessions;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Cart;
using Erewhon.Application.Services;
using Erewhon.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Api.Tests.Sessions;

/// <summary>
/// Characterises the end of an app session against the work of its requests: <see cref="SessionRegistry.Remove"/>
/// leaves no cart behind for a request that resolved the session before the removal and uses the cart after it,
/// <see cref="ClosureScheduleRegistry.Start"/> starts no schedule for a removed session, and later requests with the
/// removed id answer 401. The in-process cases use the production registries directly; the HTTP cases run the real API
/// on Kestrel over the assembly database, with one test-only endpoint under <c>/api/app</c> that waits on a barrier
/// after session resolution. Every interleaving is ordered by barriers, never by elapsed time.
/// </summary>
public sealed class SessionLifetimeTests
{
    private const string LogoutPath = "api/app/sessions/current";

    private const string LogonOptionsPath = "api/app/logon-options?area=shop";

    private const string HeldPath = "/api/app/test-lifetime/held";

    private const long CartItemId = 9_000_000_001;

    private const int ScheduleMinutes = 1;

    private static readonly DateOnly ScheduleDate = new(2026, 1, 15);

    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The state of the session id a schedule is started for.</summary>
    public enum SessionState
    {
        /// <summary>Removed while a lease taken before the removal is still held.</summary>
        RemovedWhileLeased,

        /// <summary>Removed with no lease held.</summary>
        Removed,

        /// <summary>Never stored in the registry.</summary>
        NeverStored,
    }

    /// <summary>The work the held request does once it resumes after the logout.</summary>
    public enum HeldWork
    {
        /// <summary>Adds a line to the session's cart through <see cref="ICartStore.GetCart"/>.</summary>
        AddCartLine,

        /// <summary>Empties the session's cart through <see cref="ShoppingCartService.Empty"/>.</summary>
        EmptyCart,

        /// <summary>Starts the session's closure schedule through <see cref="ClosureScheduleRegistry.Start"/>.</summary>
        StartSchedule,
    }

    /// <summary>Each state of a session id that is not stored.</summary>
    public static TheoryData<SessionState> EndedSessionStates => new()
    {
        SessionState.RemovedWhileLeased,
        SessionState.Removed,
        SessionState.NeverStored,
    };

    /// <summary>Each work of a request resumed after its session was logged out.</summary>
    public static TheoryData<HeldWork> HeldWorks => new()
    {
        HeldWork.AddCartLine,
        HeldWork.EmptyCart,
        HeldWork.StartSchedule,
    };

    /// <summary>
    /// <see cref="ClosureScheduleRegistry.Start"/> for a session id the registry does not hold returns false, leaves
    /// the id without a schedule and adds nothing to <see cref="ClosureScheduleRegistry.Snapshot"/>; a removed session
    /// raises <see cref="SessionRegistry.SessionRemoved"/> once and is no longer found by
    /// <see cref="SessionRegistry.TryGet"/>.
    /// </summary>
    /// <param name="state">The state of the session id when the schedule is started.</param>
    [Theory]
    [MemberData(nameof(EndedSessionStates))]
    public void StartForSessionNotHeldStartsNoSchedule(SessionState state)
    {
        var sessions = new SessionRegistry(new InMemoryCartStore());
        using var schedules = new ClosureScheduleRegistry(sessions);
        var removed = new List<string>();
        sessions.SessionRemoved += removed.Add;

        string sessionId = NewAdministratorSession(sessions).SessionId;
        SessionLease? lease = null;
        if (state == SessionState.RemovedWhileLeased)
        {
            Assert.True(sessions.TryAcquire(sessionId, out lease));
        }

        if (state == SessionState.NeverStored)
        {
            sessionId = Guid.NewGuid().ToString("N");
        }
        else
        {
            Assert.True(sessions.Remove(sessionId));
            Assert.Equal(new[] { sessionId }, removed);
        }

        Assert.False(schedules.Start(sessionId, ScheduleDate, ScheduleMinutes));

        Assert.False(sessions.TryGet(sessionId, out _));
        Assert.False(schedules.TryGet(sessionId, out _));
        Assert.Empty(schedules.Snapshot());

        lease?.Dispose();
        Assert.Empty(schedules.Snapshot());
    }

    /// <summary>
    /// <see cref="ClosureScheduleRegistry.Start"/> for a stored session replaces its running schedule: with minutes × 60
    /// greater than 0 it returns true and stores the date, the minutes and minutes × 60 seconds to go; with 0 or less
    /// it returns false and leaves the session without a schedule.
    /// </summary>
    /// <param name="minutes">The minutes per cycle given to the second start.</param>
    /// <param name="started">Whether the second start starts a schedule.</param>
    [Theory]
    [InlineData(1, true)]
    [InlineData(35_791_394, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(-35_791_394, false)]
    public void StartForStoredSessionReplacesSchedule(int minutes, bool started)
    {
        var sessions = new SessionRegistry(new InMemoryCartStore());
        using var schedules = new ClosureScheduleRegistry(sessions);
        string sessionId = NewAdministratorSession(sessions).SessionId;
        Assert.True(schedules.Start(sessionId, ScheduleDate, 5));

        Assert.Equal(started, schedules.Start(sessionId, ScheduleDate.AddDays(1), minutes));

        Assert.Equal(started, schedules.TryGet(sessionId, out ClosureSchedule? schedule));
        if (started)
        {
            Assert.NotNull(schedule);
            Assert.Equal(sessionId, schedule.SessionId);
            Assert.Equal(ScheduleDate.AddDays(1), schedule.Date);
            Assert.Equal(minutes, schedule.Minutes);
            Assert.Equal(minutes * ClosureScheduleRegistry.SecondsPerMinute, schedule.SecondsToGo);
            Assert.Equal(new[] { schedule }, schedules.Snapshot());
        }
        else
        {
            Assert.Empty(schedules.Snapshot());
        }
    }

    /// <summary>
    /// <see cref="ClosureScheduleRegistry.Start"/> for a stored session with minutes × 60 outside the <see cref="int"/>
    /// range throws <see cref="OverflowException"/> after removing the session's running schedule.
    /// </summary>
    /// <param name="minutes">The minutes per cycle given to the second start.</param>
    [Theory]
    [InlineData(35_791_395)]
    [InlineData(int.MaxValue)]
    [InlineData(-35_791_395)]
    public void StartForStoredSessionOverflowThrowsAfterRemovingSchedule(int minutes)
    {
        var sessions = new SessionRegistry(new InMemoryCartStore());
        using var schedules = new ClosureScheduleRegistry(sessions);
        string sessionId = NewAdministratorSession(sessions).SessionId;
        Assert.True(schedules.Start(sessionId, ScheduleDate, 5));

        Assert.Throws<OverflowException>(() => schedules.Start(sessionId, ScheduleDate, minutes));

        Assert.False(schedules.TryGet(sessionId, out _));
        Assert.Empty(schedules.Snapshot());
    }

    /// <summary>
    /// A cart used after <see cref="SessionRegistry.Remove"/> by a request that took its lease before the removal is
    /// discarded when the last lease is disposed: the removal raises <see cref="SessionRegistry.SessionRemoved"/> at
    /// once, no further lease is taken, the cart and its line stay while any lease is held, a second disposal of a lease
    /// changes nothing, and after the last disposal no cart holds the line and the id gets a new empty cart.
    /// </summary>
    /// <param name="leaseCount">The number of leases held when the session is removed.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CartUsedAfterRemoveIsDiscardedWithLastLease(int leaseCount)
    {
        var carts = new InMemoryCartStore();
        var sessions = new SessionRegistry(carts);
        var removed = new List<string>();
        sessions.SessionRemoved += removed.Add;
        SessionEntry entry = NewClientSession(sessions);

        var leases = new List<SessionLease>();
        for (var i = 0; i < leaseCount; i++)
        {
            Assert.True(sessions.TryAcquire(entry.SessionId, out SessionLease? lease));
            Assert.Same(entry, lease.Entry);
            leases.Add(lease);
        }

        Assert.True(sessions.Remove(entry.SessionId));
        Assert.Equal(new[] { entry.SessionId }, removed);
        Assert.False(sessions.TryAcquire(entry.SessionId, out SessionLease? refused));
        Assert.Null(refused);
        Assert.False(sessions.Remove(entry.SessionId));
        Assert.Single(removed);

        ShoppingCart resumed = carts.GetCart(entry.SessionId);
        resumed.Add(NewCartLine());

        foreach (SessionLease earlier in leases.Take(leaseCount - 1))
        {
            earlier.Dispose();
            earlier.Dispose();
            Assert.Same(resumed, carts.GetCart(entry.SessionId));
            Assert.True(resumed.Contains(CartItemId));
        }

        leases[^1].Dispose();
        leases[^1].Dispose();

        Assert.Empty(carts.RemoveItemFromAllCarts(CartItemId));
        ShoppingCart later = carts.GetCart(entry.SessionId);
        Assert.NotSame(resumed, later);
        Assert.True(later.IsEmpty);
    }

    /// <summary>
    /// <see cref="SessionRegistry.Remove"/> with no lease held discards the session's cart before it raises
    /// <see cref="SessionRegistry.SessionRemoved"/>; a lease taken and disposed earlier leaves the cart of the stored
    /// session in place.
    /// </summary>
    [Fact]
    public void RemoveWithoutLeaseDiscardsCartBeforeSessionRemoved()
    {
        var carts = new InMemoryCartStore();
        var sessions = new SessionRegistry(carts);
        SessionEntry entry = NewClientSession(sessions);
        ShoppingCart cart = carts.GetCart(entry.SessionId);
        cart.Add(NewCartLine());

        Assert.True(sessions.TryAcquire(entry.SessionId, out SessionLease? lease));
        lease.Dispose();
        Assert.Same(cart, carts.GetCart(entry.SessionId));
        Assert.True(cart.Contains(CartItemId));

        var cartsHoldingItemAtEvent = new List<IReadOnlyList<string>>();
        sessions.SessionRemoved += _ => cartsHoldingItemAtEvent.Add(carts.RemoveItemFromAllCarts(CartItemId));

        Assert.True(sessions.Remove(entry.SessionId));

        IReadOnlyList<string> atEvent = Assert.Single(cartsHoldingItemAtEvent);
        Assert.Empty(atEvent);
        Assert.NotSame(cart, carts.GetCart(entry.SessionId));
    }

    /// <summary>
    /// <see cref="SessionRegistry.TryAcquire"/> and <see cref="SessionRegistry.Remove"/> find nothing for a null, empty,
    /// white-space or never-stored id, and <see cref="SessionRegistry.SessionRemoved"/> is not raised.
    /// </summary>
    /// <param name="sessionId">The id looked up.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void UnknownIdTakesNoLeaseAndRemovesNothing(string? sessionId)
    {
        var sessions = new SessionRegistry(new InMemoryCartStore());
        NewClientSession(sessions);
        var removed = new List<string>();
        sessions.SessionRemoved += removed.Add;

        Assert.False(sessions.TryAcquire(sessionId, out SessionLease? lease));
        Assert.Null(lease);
        Assert.False(sessions.Remove(sessionId!));
        Assert.Empty(removed);
    }

    /// <summary>
    /// Over HTTP: a request to an <c>/api/app</c> endpoint resolves its session and waits; the session logs out with
    /// <c>DELETE /api/app/sessions/current</c>, which answers 204; the request then resumes and does
    /// <paramref name="work"/> for its session and answers 204. Afterwards no cart and no closure schedule exist for the
    /// id, a schedule start made by the resumed request reports false, and later requests with the id answer 401.
    /// </summary>
    /// <param name="work">The work the resumed request does.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [MemberData(nameof(HeldWorks))]
    public async Task RequestResumedAfterLogoutLeavesNoCartOrSchedule(HeldWork work)
    {
        var held = new HeldRequest(work);
        await using ErewhonApiHost host = await StartHostAsync(held);
        using HttpClient client = host.CreateHttpClient();
        var sessions = host.Services.GetRequiredService<SessionRegistry>();
        var carts = host.Services.GetRequiredService<ICartStore>();
        var schedules = host.Services.GetRequiredService<ClosureScheduleRegistry>();

        SessionEntry session = work == HeldWork.StartSchedule
            ? NewAdministratorSession(sessions)
            : NewClientSession(sessions);
        string sessionId = session.SessionId;

        try
        {
            Task<HttpResponseMessage> running = client.SendAsync(SessionRequest(HttpMethod.Post, HeldPath, sessionId));
            Assert.Same(session, await held.Resolved.Task.WaitAsync(BarrierTimeout));

            using (HttpResponseMessage logout = await client.SendAsync(SessionRequest(HttpMethod.Delete, LogoutPath, sessionId)))
            {
                Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            }

            Assert.False(sessions.TryGet(sessionId, out _));
            Assert.False(held.Completed.Task.IsCompleted);

            held.Resume.SetResult();
            using HttpResponseMessage resumed = await running.WaitAsync(BarrierTimeout);
            Assert.Equal(HttpStatusCode.NoContent, resumed.StatusCode);
            HeldOutcome outcome = await held.Completed.Task.WaitAsync(BarrierTimeout);

            Assert.DoesNotContain(sessionId, carts.RemoveItemFromAllCarts(CartItemId));
            AssertNoCart(carts, sessionId, outcome.Cart);

            Assert.False(outcome.ScheduleStarted);
            Assert.False(schedules.TryGet(sessionId, out _));
            Assert.DoesNotContain(schedules.Snapshot(), schedule => schedule.SessionId == sessionId);

            await AssertRefusedAsync(client, sessionId);
            Assert.False(sessions.TryAcquire(sessionId, out _));
        }
        finally
        {
            held.Resume.TrySetResult();
        }
    }

    /// <summary>
    /// Over HTTP: with no other request of the session running, and after a bootstrap request that carried the session
    /// header has ended, <c>DELETE /api/app/sessions/current</c> has discarded the session's cart and its running
    /// closure schedule when its 204 arrives, and later requests with the id answer 401.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task LogoutDiscardsCartAndScheduleBeforeItsResponse()
    {
        var held = new HeldRequest(HeldWork.AddCartLine);
        await using ErewhonApiHost host = await StartHostAsync(held);
        using HttpClient client = host.CreateHttpClient();
        var sessions = host.Services.GetRequiredService<SessionRegistry>();
        var carts = host.Services.GetRequiredService<ICartStore>();
        var schedules = host.Services.GetRequiredService<ClosureScheduleRegistry>();

        string sessionId = NewAdministratorSession(sessions).SessionId;
        ShoppingCart cart = carts.GetCart(sessionId);
        cart.Add(NewCartLine());
        Assert.True(schedules.Start(sessionId, ScheduleDate, ScheduleMinutes));

        using (HttpResponseMessage options = await client.SendAsync(SessionRequest(HttpMethod.Get, LogonOptionsPath, sessionId)))
        {
            Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        }

        using (HttpResponseMessage logout = await client.SendAsync(SessionRequest(HttpMethod.Delete, LogoutPath, sessionId)))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        Assert.Empty(carts.RemoveItemFromAllCarts(CartItemId));
        AssertNoCart(carts, sessionId, cart);
        Assert.False(schedules.TryGet(sessionId, out _));
        Assert.Empty(schedules.Snapshot());

        await AssertRefusedAsync(client, sessionId);
        Assert.False(held.Resolved.Task.IsCompleted);
    }

    /// <summary>Creates a stored administrator session of the admin area.</summary>
    private static SessionEntry NewAdministratorSession(SessionRegistry sessions) =>
        sessions.Create(LogonArea.Admin, SessionRole.Administrator, agentId: null, clientId: null, "Lifetime Company");

    /// <summary>Creates a stored client session of the shop area.</summary>
    private static SessionEntry NewClientSession(SessionRegistry sessions) =>
        sessions.Create(LogonArea.Shop, SessionRole.Client, agentId: null, clientId: 1, "Lifetime Client");

    /// <summary>Returns a new retail cart line for <see cref="CartItemId"/>.</summary>
    private static CartItem NewCartLine() => new()
    {
        ItemId = CartItemId,
        IsTender = false,
        Code = "LIF1",
        Transaction = "Retail",
        Offer = 0m,
        SalePrice = 10m,
        ClientId = 1,
    };

    /// <summary>Returns a request to <paramref name="path"/> carrying <paramref name="sessionId"/> as the session header.</summary>
    private static HttpRequestMessage SessionRequest(HttpMethod method, string path, string sessionId)
    {
        var request = new HttpRequestMessage(method, path.TrimStart('/'));
        request.Headers.Add(SessionMiddleware.HeaderName, sessionId);
        return request;
    }

    /// <summary>
    /// Asserts that the store held no cart for <paramref name="sessionId"/>: the id gets a new empty cart, not
    /// <paramref name="used"/>. Removes that new cart afterwards.
    /// </summary>
    private static void AssertNoCart(ICartStore carts, string sessionId, ShoppingCart? used)
    {
        ShoppingCart current = carts.GetCart(sessionId);
        try
        {
            Assert.True(current.IsEmpty);
            if (used is not null)
            {
                Assert.NotSame(used, current);
            }
        }
        finally
        {
            carts.RemoveCart(sessionId);
        }
    }

    /// <summary>Asserts that the logout route and the held route answer 401 for <paramref name="sessionId"/>.</summary>
    private static async Task AssertRefusedAsync(HttpClient client, string sessionId)
    {
        foreach ((HttpMethod method, string path) in new[] { (HttpMethod.Delete, LogoutPath), (HttpMethod.Post, HeldPath) })
        {
            using HttpResponseMessage refused = await client.SendAsync(SessionRequest(method, path, sessionId));
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }
    }

    /// <summary>
    /// Starts the real API over the assembly database with <see cref="HeldPath"/> mapped to
    /// <see cref="HoldAsync"/> for <paramref name="held"/>.
    /// </summary>
    private static async Task<ErewhonApiHost> StartHostAsync(HeldRequest held)
    {
        PostgresDatabase database = PostgresDatabase.ForAssembly(typeof(SessionLifetimeTests).Assembly);
        await database.EnsureCreatedAndMigratedAsync();

        return await ErewhonApiHost.StartAsync(new ErewhonApiHostOptions
        {
            ConnectionString = database.ConnectionString,
            MapEndpoints = app => app.MapPost(
                HeldPath,
                (Func<HttpContext, Task<IResult>>)(context => HoldAsync(context, held))),
        });
    }

    /// <summary>
    /// The held endpoint: reports the session <see cref="SessionMiddleware"/> resolved, waits until
    /// <see cref="HeldRequest.Resume"/> completes, does the work of <paramref name="held"/> for that session, reports
    /// the outcome and answers 204.
    /// </summary>
    private static async Task<IResult> HoldAsync(HttpContext context, HeldRequest held)
    {
        SessionEntry entry = SessionMiddleware.GetSession(context)
            ?? throw new InvalidOperationException("The held request reached its endpoint without a session.");
        held.Resolved.TrySetResult(entry);

        await held.Resume.Task;

        try
        {
            held.Completed.TrySetResult(DoWork(context.RequestServices, entry.SessionId, held.Work));
        }
        catch (Exception failure)
        {
            held.Completed.TrySetException(failure);
            throw;
        }

        return Results.NoContent();
    }

    /// <summary>Does <paramref name="work"/> for <paramref name="sessionId"/> with the request's services.</summary>
    private static HeldOutcome DoWork(IServiceProvider services, string sessionId, HeldWork work)
    {
        var carts = services.GetRequiredService<ICartStore>();
        switch (work)
        {
            case HeldWork.AddCartLine:
                ShoppingCart cart = carts.GetCart(sessionId);
                cart.Add(NewCartLine());
                return new HeldOutcome(cart, ScheduleStarted: false);

            case HeldWork.EmptyCart:
                services.GetRequiredService<ShoppingCartService>().Empty(sessionId);
                return new HeldOutcome(carts.GetCart(sessionId), ScheduleStarted: false);

            case HeldWork.StartSchedule:
                bool started = services.GetRequiredService<ClosureScheduleRegistry>()
                    .Start(sessionId, ScheduleDate, ScheduleMinutes);
                return new HeldOutcome(Cart: null, started);

            default:
                throw new ArgumentOutOfRangeException(nameof(work), work, "The held work is not defined.");
        }
    }

    /// <summary>The cart the held request used, or null, and whether its schedule start started a schedule.</summary>
    /// <param name="Cart">The cart instance the held request used, or <see langword="null"/>.</param>
    /// <param name="ScheduleStarted">What <see cref="ClosureScheduleRegistry.Start"/> returned; false when not called.</param>
    private sealed record HeldOutcome(ShoppingCart? Cart, bool ScheduleStarted);

    /// <summary>The barriers shared by one held request and its test.</summary>
    /// <param name="work">The work the request does once it resumes.</param>
    private sealed class HeldRequest(HeldWork work)
    {
        /// <summary>The work the request does once it resumes.</summary>
        public HeldWork Work { get; } = work;

        /// <summary>Completed with the resolved session when the request reaches its endpoint.</summary>
        public TaskCompletionSource<SessionEntry> Resolved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completed by the test to let the request continue.</summary>
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completed with the outcome of the request's work.</summary>
        public TaskCompletionSource<HeldOutcome> Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
