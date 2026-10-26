using NewScope.Controls;

namespace NewScope.Tests;

public class AxisRangePolicyTests
{
    [Theory]
    [InlineData(-double.MaxValue, double.MaxValue)]
    [InlineData(double.MaxValue, double.MaxValue)]
    [InlineData(-double.MaxValue, -double.MaxValue)]
    public void ExtremeFiniteValuesKeepFiniteAxisEndpoints(double min, double max)
    {
        var policy = new AxisRangePolicy();
        var range = policy.Update(min, max, 1);

        Assert.True(double.IsFinite(range.Min));
        Assert.True(double.IsFinite(range.Max));
        Assert.True(range.Max > range.Min);
        Assert.True(range.Min <= min);
        Assert.True(range.Max >= max);
    }

    [Fact]
    public void FirstRangeIsFiniteAndContainsData()
    {
        var policy = new AxisRangePolicy();

        var (min, max) = policy.Update(-12.5, 87.5, 0);

        Assert.True(double.IsFinite(min));
        Assert.True(double.IsFinite(max));
        Assert.True(min < -12.5);
        Assert.True(max > 87.5);
    }

    [Fact]
    public void ExpandsImmediatelyForNewExtremes()
    {
        var policy = new AxisRangePolicy();
        var initial = policy.Update(0, 100, 0);

        var expanded = policy.Update(0, 5000, 10);

        Assert.True(expanded.Max >= 5000);
        Assert.True(expanded.Max > initial.Max);
    }

    [Fact]
    public void HoldsRangeWhileTargetRemainsBroadEnough()
    {
        var policy = new AxisRangePolicy();
        var initial = policy.Update(0, 1000, 0);

        var next = policy.Update(0, 900, 5000);

        Assert.Equal(initial, next);
    }

    [Fact]
    public void ShrinksOnlyAfterDelay()
    {
        var policy = new AxisRangePolicy();
        var initial = policy.Update(0, 100_000, 0);
        var narrow = policy.Update(0, 100, 10);

        var duringDelay = policy.Update(0, 100, 1000);
        var afterDelay = policy.Update(0, 100, 2100);

        Assert.Equal(initial, narrow);
        Assert.Equal(initial, duringDelay);
        Assert.True(afterDelay.Max - afterDelay.Min < initial.Max - initial.Min);
        Assert.True(afterDelay.Min <= 0);
        Assert.True(afterDelay.Max >= 100);
    }

    [Fact]
    public void ResetClearsRememberedRange()
    {
        var policy = new AxisRangePolicy();
        policy.Update(0, 100_000, 0);
        policy.Reset();

        var range = policy.Update(0, 100, 10);

        Assert.True(range.Max - range.Min < 10_000);
    }

    [Fact]
    public void HandlesEmptyData()
    {
        var policy = new AxisRangePolicy();

        var range = policy.Update(double.PositiveInfinity, double.NegativeInfinity, 0);

        Assert.Equal((-1, 1), range);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(2_000)]
    public void RangeIsAlwaysValid(long now)
    {
        var policy = new AxisRangePolicy();

        var range = policy.Update(0, 100, now);

        Assert.True(range.Max > range.Min);
        Assert.True(double.IsFinite(range.Min));
        Assert.True(double.IsFinite(range.Max));
    }
}
