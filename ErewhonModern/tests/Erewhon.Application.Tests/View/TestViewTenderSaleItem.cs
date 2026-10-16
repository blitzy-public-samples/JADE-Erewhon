namespace Erewhon.Application.Tests.View;

/// <summary>
/// Tests of the tender item's offer check on the <see cref="TestViewSaleItem"/> fixture.
/// Ports the JADE class <c>TestViewTenderSaleItem</c>.
/// </summary>
public sealed class TestViewTenderSaleItem : TestViewSaleItem
{
    /// <summary>
    /// The tender item refuses the offers null (0), -1, -5.50, 0, its price - 1 and its price - 0.01, and accepts its
    /// price and <c>Max_Integer</c> (2147483647).
    /// </summary>
    [Fact]
    public void testCheckOffer()
    {
        // Arrange: tender item price
        decimal price = MyTenderItem.Price;

        // Assert: offers below the price are refused
        Assert.False(MyTenderItem.CheckOffer(0m));
        Assert.False(MyTenderItem.CheckOffer(-1m));
        Assert.False(MyTenderItem.CheckOffer(-5.50m));
        Assert.False(MyTenderItem.CheckOffer(0m));
        Assert.False(MyTenderItem.CheckOffer(price - 1m));
        Assert.False(MyTenderItem.CheckOffer(price - 0.01m));

        // Assert: offers of at least the price are accepted
        Assert.True(MyTenderItem.CheckOffer(price));
        Assert.True(MyTenderItem.CheckOffer(int.MaxValue));
    }
}
