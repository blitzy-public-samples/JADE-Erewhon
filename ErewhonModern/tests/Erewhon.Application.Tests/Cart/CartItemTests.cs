using Erewhon.Application.Cart;

namespace Erewhon.Application.Tests.Cart;

public sealed class CartItemTests
{
    private const int Callers = 32;
    private const int Iterations = 200;

    private static readonly DateTime FirstCandidate = new(2024, 3, 15, 10, 30, 0, 125, DateTimeKind.Unspecified);

    [Fact]
    public async Task OverlappingRetainDateSoldCallsReturnOneRetainedTimestamp()
    {
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var cart = new ShoppingCart("session-a");
            cart.Add(RetailLine(iteration + 1));
            var line = cart.Items[0];
            var candidates = Enumerable.Range(0, Callers)
                .Select(caller => FirstCandidate.AddMilliseconds((iteration * Callers) + caller))
                .ToArray();

            // Each caller runs on its own thread, and the barrier releases every caller at once.
            using var barrier = new Barrier(Callers);
            var calls = candidates
                .Select(candidate => Task.Factory.StartNew(
                    () =>
                    {
                        barrier.SignalAndWait();
                        return line.RetainDateSold(candidate);
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default))
                .ToArray();
            var returned = await Task.WhenAll(calls);

            var retained = returned[0];
            Assert.All(returned, value => Assert.Equal(retained, value));
            Assert.Contains(retained, candidates);
            Assert.Equal(retained, line.DateSold);
            Assert.Equal(retained, cart.Items[0].DateSold);
        }
    }

    [Fact]
    public void LaterRetainDateSoldCallKeepsFirstTimestamp()
    {
        var cart = new ShoppingCart("session-a");
        var line = RetailLine(1);
        cart.Add(line);
        var first = FirstCandidate;
        var later = FirstCandidate.AddSeconds(5);

        Assert.Equal(first, line.RetainDateSold(first));
        Assert.Equal(first, line.RetainDateSold(later));

        Assert.Equal(first, line.DateSold);
        Assert.Equal(DateTimeKind.Unspecified, line.DateSold!.Value.Kind);
        Assert.Equal(first, cart.Items[0].DateSold);
    }

    [Fact]
    public void RetainDateSoldRejectsNullTimestamp()
    {
        var line = RetailLine(1);

        Assert.Throws<ArgumentOutOfRangeException>("dateSold", () => line.RetainDateSold(default));

        Assert.Null(line.DateSold);
    }

    [Fact]
    public void RetainDateSoldRejectsTenderLine()
    {
        var line = new CartItem
        {
            ItemId = 1,
            IsTender = true,
            Code = "TEND1",
            Transaction = "Tender",
            Offer = 250.50m,
            ClientId = 7,
            DateTendered = FirstCandidate,
        };

        Assert.Throws<InvalidOperationException>(() => line.RetainDateSold(FirstCandidate.AddSeconds(5)));

        Assert.Null(line.DateSold);
    }

    [Fact]
    public void DateSoldHasNoSetter()
    {
        var property = typeof(CartItem).GetProperty(nameof(CartItem.DateSold));

        Assert.NotNull(property);
        Assert.Null(property.SetMethod);
    }

    private static CartItem RetailLine(long itemId) => new()
    {
        ItemId = itemId,
        IsTender = false,
        Code = "RETL" + itemId,
        Transaction = "Retail",
        Offer = 0m,
        SalePrice = 100.00m,
        ClientId = 7,
    };
}
