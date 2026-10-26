using NewScope.Services;

namespace NewScope.Tests;

public class SampleMemoryBudgetTests
{
    [Fact]
    public void NormalChannelCountKeepsSixtySecondRetention()
    {
        var capacity = SampleMemoryBudget.GetCapacity(128, 2_000);

        Assert.Equal(120_000, capacity);
        Assert.False(SampleMemoryBudget.IsConstrained(128, 2_000));
    }

    [Fact]
    public void HighChannelCountIsCappedByMemoryBudget()
    {
        var capacity = SampleMemoryBudget.GetCapacity(359, 2_000);

        Assert.True(capacity < 120_000);
        Assert.True(capacity >= SampleMemoryBudget.MinimumChannelCapacity);
        Assert.True(SampleMemoryBudget.IsConstrained(359, 2_000));
    }

    [Fact]
    public void EmptyOrInvalidInputFallsBackToDefaultCapacity()
    {
        Assert.Equal(SampleMemoryBudget.DefaultChannelCapacity, SampleMemoryBudget.GetCapacity(0, 100));
        Assert.Equal(SampleMemoryBudget.DefaultChannelCapacity, SampleMemoryBudget.GetCapacity(10, 0));
        Assert.False(SampleMemoryBudget.IsConstrained(0, 0));
    }
}
