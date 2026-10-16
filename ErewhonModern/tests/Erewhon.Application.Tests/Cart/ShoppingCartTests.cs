using Erewhon.Application.Cart;
using Erewhon.Application.Common;

namespace Erewhon.Application.Tests.Cart;

public sealed class ShoppingCartTests
{
    private const int CheckedOutLines = 64;
    private const int RacingLines = 64;
    private const int Iterations = 200;

    [Theory]
    [InlineData(new long[] { 1, 2, 3 }, new long[] { 2 }, 1, new long[] { 1, 3 })]
    [InlineData(new long[] { 1, 2, 3 }, new long[] { 1, 2, 3 }, 3, new long[0])]
    [InlineData(new long[] { 1, 2, 3 }, new long[0], 0, new long[] { 1, 2, 3 })]
    [InlineData(new long[] { 1, 2, 3 }, new long[] { 3, 3 }, 1, new long[] { 1, 2 })]
    [InlineData(new long[] { 1, 2, 3 }, new long[] { 4 }, 0, new long[] { 1, 2, 3 })]
    [InlineData(new long[0], new long[] { 1 }, 0, new long[0])]
    public void RemoveLinesRemovesOnlyTheGivenLineObjects(
        long[] cartIds,
        long[] givenIds,
        int expectedRemoved,
        long[] remainingIds)
    {
        var cart = new ShoppingCart("session-a");
        foreach (var id in cartIds)
        {
            cart.Add(RetailLine(id));
        }

        var held = cart.Items.ToDictionary(line => line.ItemId);
        var given = givenIds
            .Select(id => held.TryGetValue(id, out var line) ? line : RetailLine(id))
            .ToArray();

        var removed = cart.RemoveLines(given);

        Assert.Equal(expectedRemoved, removed);
        Assert.Equal(remainingIds, cart.Items.Select(line => line.ItemId));
        Assert.All(cart.Items, line => Assert.Same(held[line.ItemId], line));
    }

    [Fact]
    public void RemoveLinesKeepsReplacementLineForSameItemId()
    {
        var cart = new ShoppingCart("session-a");
        cart.Add(RetailLine(1));
        cart.Add(RetailLine(2));
        cart.Add(RetailLine(3));
        var checkedOut = cart.Items;
        var replacement = RetailLine(2);
        cart.Remove(2);
        cart.Add(replacement);

        var removed = cart.RemoveLines(checkedOut);

        Assert.Equal(2, removed);
        var remaining = Assert.Single(cart.Items);
        Assert.Same(replacement, remaining);
    }

    [Fact]
    public async Task RemoveLinesKeepsLinesAddedWhileItRuns()
    {
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var cart = new ShoppingCart("session-a");
            for (long id = 1; id <= CheckedOutLines; id++)
            {
                cart.Add(RetailLine(id));
            }

            var checkedOut = cart.Items;
            var added = Enumerable.Range(CheckedOutLines + 1, RacingLines)
                .Select(id => RetailLine(id))
                .ToArray();

            // The remover and the adder each run on their own thread, and the barrier releases both at once.
            using var barrier = new Barrier(2);
            var remover = Task.Factory.StartNew(
                () =>
                {
                    barrier.SignalAndWait();
                    return cart.RemoveLines(checkedOut);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            var adder = Task.Factory.StartNew(
                () =>
                {
                    barrier.SignalAndWait();
                    foreach (var line in added)
                    {
                        cart.Add(line);
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            await Task.WhenAll(remover, adder);

            Assert.Equal(CheckedOutLines, await remover);
            Assert.Equal(added, cart.Items);
        }
    }

    [Fact]
    public async Task RemoveLinesKeepsLinesReplacedWhileItRuns()
    {
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var cart = new ShoppingCart("session-a");
            for (long id = 1; id <= CheckedOutLines; id++)
            {
                cart.Add(RetailLine(id));
            }

            var checkedOut = cart.Items;
            var replacements = checkedOut
                .Where(line => line.ItemId % 2 == 0)
                .Select(line => RetailLine(line.ItemId))
                .ToArray();

            // The remover and the replacer each run on their own thread, and the barrier releases both at once.
            using var barrier = new Barrier(2);
            var remover = Task.Factory.StartNew(
                () =>
                {
                    barrier.SignalAndWait();
                    return cart.RemoveLines(checkedOut);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            var replacer = Task.Factory.StartNew(
                () =>
                {
                    barrier.SignalAndWait();
                    foreach (var line in replacements)
                    {
                        cart.Remove(line.ItemId);
                        cart.Add(line);
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            await Task.WhenAll(remover, replacer);

            Assert.InRange(await remover, CheckedOutLines - replacements.Length, CheckedOutLines);
            Assert.Equal(replacements, cart.Items);
        }
    }

    [Fact]
    public void RemoveLinesRejectsNullLines()
    {
        var cart = new ShoppingCart("session-a");
        cart.Add(RetailLine(1));

        Assert.Throws<ArgumentNullException>("items", () => cart.RemoveLines(null!));

        Assert.Equal(1, cart.Size);
    }

    [Fact]
    public void RemoveLinesRejectsNullLineAndLeavesCartUnchanged()
    {
        var cart = new ShoppingCart("session-a");
        cart.Add(RetailLine(1));
        cart.Add(RetailLine(2));
        var held = cart.Items;

        Assert.Throws<ArgumentException>("items", () => cart.RemoveLines(new[] { held[0], null!, held[1] }));

        Assert.Equal(held, cart.Items);
    }

    [Fact]
    public void AddOfHeldItemIdThrowsKeyAlreadyUsedAndKeepsHeldLine()
    {
        var cart = new ShoppingCart("session-a");
        var held = RetailLine(1);
        cart.Add(held);
        cart.Add(RetailLine(2));

        var failure = Assert.Throws<KeyAlreadyUsedException>(() => cart.Add(RetailLine(1)));

        Assert.Equal(1310, failure.ErrorCode);
        Assert.Equal("Key already used in this dictionary", failure.Message);
        Assert.Equal(new long[] { 1, 2 }, cart.Items.Select(line => line.ItemId));
        Assert.Same(held, cart.Items[0]);
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
