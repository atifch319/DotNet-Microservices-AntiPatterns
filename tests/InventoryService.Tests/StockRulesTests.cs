using Commerce.Inventory.Domain;

namespace InventoryService.Tests;

public class StockRulesTests
{
    [Theory]
    [InlineData(10, 1, true)]
    [InlineData(1, 1, true)]
    [InlineData(0, 1, false)]
    [InlineData(5, 6, false)]
    [InlineData(5, 0, false)]
    public void Reservation_requires_enough_available_quantity(int available, int requested, bool expected)
    {
        Assert.Equal(expected, StockRules.CanReserve(available, requested));
    }
}
