using NewScope.Models;

namespace NewScope.Tests;

public class ChannelBufferTests
{
    [Fact]
    public void AddKeepsNewestSamplesWhenAtCapacity()
    {
        var buffer = new ChannelBuffer(4);

        for (var i = 0; i < 10; i++)
        {
            buffer.Add(i, i * 10);
        }

        Assert.Equal(4, buffer.Count);
        Assert.Equal(4, buffer.Capacity);
        Assert.Equal([new SamplePoint(6, 60), new SamplePoint(7, 70), new SamplePoint(8, 80),
            new SamplePoint(9, 90)], buffer.ToArray());
    }

    [Fact]
    public void ClearRemovesAllSamples()
    {
        var buffer = new ChannelBuffer();
        buffer.Add(1, 2);

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Empty(buffer.ToArray());
        Assert.False(buffer.TryGetLatest(out _));
    }

    [Fact]
    public void GetStatsSkipsNonFiniteValues()
    {
        var buffer = new ChannelBuffer();
        buffer.Add(1, 10);
        buffer.Add(2, double.NaN);
        buffer.Add(3, 20);
        buffer.Add(4, 30);

        var stats = buffer.GetStats();

        Assert.Equal(10, stats.Min);
        Assert.Equal(30, stats.Max);
        Assert.Equal(20, stats.Average, 6);
        Assert.Equal(30, stats.Last);
        Assert.Equal(4, stats.Count);
    }

    [Fact]
    public void CopyVisibleReturnsBoundedDataAndAlwaysIncludesLatest()
    {
        var buffer = new ChannelBuffer();
        for (var i = 0; i < 100; i++)
        {
            buffer.Add(i, i);
        }

        var destination = new SamplePoint[11];
        var count = buffer.CopyVisible(10, 50, destination, 10);

        Assert.InRange(count, 2, 11);
        Assert.Equal(10, destination[0].Time);
        Assert.Equal(50, destination[count - 1].Time);
    }

    [Fact]
    public void CopyVisibleReturnsEmptyOutsideRange()
    {
        var buffer = new ChannelBuffer();
        buffer.Add(10, 1);

        var destination = new SamplePoint[4];

        Assert.Equal(0, buffer.CopyVisible(20, 30, destination, 4));
        Assert.Equal(0, buffer.CopyVisible(0, 5, destination, 4));
    }


    [Fact]
    public void FindsExactSampleTime()
    {
        var buffer = new ChannelBuffer();
        buffer.Add(1.5, 42);

        Assert.True(buffer.TryGetValueAtTime(1.5, out var value));
        Assert.Equal(42, value);
        Assert.False(buffer.TryGetValueAtTime(1.6, out _));
    }

    [Fact]
    public void BufferGrowsUpToConfiguredMaximum()
    {
        var buffer = new ChannelBuffer(4_096);

        for (var i = 0; i < 3_000; i++)
        {
            buffer.Add(i / 100.0, i);
        }

        Assert.Equal(3_000, buffer.Count);
        Assert.True(buffer.Capacity >= 3_000);
        Assert.True(buffer.Capacity <= 4_096);
    }

    [Fact]
    public void SetMaxCapacityDropsOldestSamples()
    {
        var buffer = new ChannelBuffer(4_096);
        for (var i = 0; i < 1_000; i++)
        {
            buffer.Add(i, i * 10);
        }

        buffer.SetMaxCapacity(100);

        Assert.Equal(100, buffer.MaxCapacity);
        Assert.Equal(100, buffer.Count);
        Assert.Equal(100, buffer.Capacity);
        Assert.Equal(900, buffer.ToArray()[0].Time);
        Assert.Equal(999, buffer.ToArray()[^1].Time);
    }

    [Fact]
    public void BufferCanGrowAgainAfterCapacityLimitIsRaised()
    {
        var buffer = new ChannelBuffer(4_096);
        for (var i = 0; i < 500; i++)
        {
            buffer.Add(i, i);
        }

        buffer.SetMaxCapacity(100);
        buffer.SetMaxCapacity(1_000);
        for (var i = 500; i < 2_000; i++)
        {
            buffer.Add(i / 100.0, i);
        }

        Assert.Equal(1_000, buffer.MaxCapacity);
        Assert.Equal(1_000, buffer.Count);
        Assert.Equal(1_000, buffer.Capacity);
        Assert.Equal(10.0, buffer.ToArray()[0].Time, 6);
        Assert.Equal(19.99, buffer.ToArray()[^1].Time, 6);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(64)]
    public void DownsamplingPreservesPositiveAndNegativeSpikesAfterRingWrap(int budget)
    {
        var buffer = new ChannelBuffer(128);
        for (var i = 0; i < 300; i++)
        {
            buffer.Add(i, i == 251 ? 1000 : i == 252 ? -800 : 0);
        }

        var destination = new SamplePoint[budget];
        var count = buffer.CopyVisible(200, 299, destination, budget);
        var points = destination.Take(count).ToArray();

        Assert.InRange(count, 4, budget);
        Assert.Equal(200, points[0].Time);
        Assert.Equal(299, points[^1].Time);
        Assert.Equal(1000, points.Max(point => point.Value));
        Assert.Equal(-800, points.Min(point => point.Value));
        Assert.True(points.Zip(points.Skip(1)).All(pair => pair.First.Time < pair.Second.Time));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void DownsamplingHonorsTheDestinationSizeAndKeepsTheLastPoint(int size)
    {
        var buffer = new ChannelBuffer(100);
        for (var i = 0; i < 100; i++)
        {
            buffer.Add(i, i);
        }

        var points = new SamplePoint[size];
        var count = buffer.CopyVisible(0, 99, points, 1000);

        Assert.InRange(count, 1, size);
        Assert.Equal(99, points[count - 1].Time);
    }

    [Fact]
    public void SummariesMatchRawSamplesAcrossGrowthWrapShrinkAndClear()
    {
        var random = new Random(1729);
        var buffer = new ChannelBuffer(4096);
        for (var i = 0; i < 6000; i++)
        {
            if (i == 3100)
            {
                buffer.SetMaxCapacity(257);
            }
            else if (i == 4500)
            {
                buffer.SetMaxCapacity(1024);
            }

            buffer.Add(i / 2000.0, i % 17 == 0 ? double.NaN : random.Next(-1000, 1001));
            if (i % 31 == 0)
            {
                AssertStatsMatchSamples(buffer);
            }
        }

        AssertStatsMatchSamples(buffer);
        buffer.Clear();
        buffer.Add(0, double.NaN);
        buffer.Add(1, 42);
        AssertStatsMatchSamples(buffer);
        buffer.Clear();
        Assert.Equal(new ChannelStats(0, 0, 0, 0, 0), buffer.GetStats());
    }

    private static void AssertStatsMatchSamples(ChannelBuffer buffer)
    {
        var samples = buffer.ToArray();
        var values = samples.Select(point => point.Value).Where(double.IsFinite).ToArray();
        var stats = buffer.GetStats();
        Assert.Equal(samples.Length, stats.Count);
        if (values.Length == 0)
        {
            return;
        }

        Assert.Equal(values.Min(), stats.Min);
        Assert.Equal(values.Max(), stats.Max);
        Assert.Equal(values.Average(), stats.Average, 9);
        Assert.Equal(values[^1], stats.Last);
    }

    [Fact]
    public void VisibleRangeAndNearestPointMatchRawDataAcrossRingWrap()
    {
        var buffer = new ChannelBuffer(257);
        for (var i = 0; i < 1000; i++)
        {
            buffer.Add(i / 1000.0, i % 41 == 0 ? double.NaN : i == 851 ? 10000 : -i);
        }

        var raw = buffer.ToArray();
        var random = new Random(42);
        for (var i = 0; i < 100; i++)
        {
            var start = 0.7 + random.NextDouble() * 0.25;
            var end = start + random.NextDouble() * 0.1;
            var visible = raw.Where(point => point.Time >= start && point.Time <= end).ToArray();
            var finite = visible.Where(point => point.IsValid).ToArray();
            Assert.Equal(finite.Length > 0, buffer.TryGetVisibleRange(start, end, out var min, out var max));
            if (finite.Length > 0)
            {
                Assert.Equal(finite.Min(point => point.Value), min);
                Assert.Equal(finite.Max(point => point.Value), max);
            }

            var time = start + random.NextDouble() * (end - start);
            Assert.Equal(visible.Length > 0, buffer.TryGetNearest(time, start, end, out var nearest));
            if (visible.Length > 0)
            {
                Assert.Equal(visible.OrderBy(point => Math.Abs(point.Time - time)).First(), nearest);
            }
        }
    }

    [Fact]
    public void NearestPointUsesRawSamplesRatherThanTheDownsampledEnvelope()
    {
        var buffer = new ChannelBuffer(1000);
        for (var i = 0; i < 1000; i++) buffer.Add(i, i == 500 ? 1000 : 0);

        Assert.True(buffer.TryGetNearest(501, 0, 999, out var point));
        Assert.Equal(new SamplePoint(501, 0), point);
        Assert.True(buffer.TryGetNearest(0, 100, 200, out point));
        Assert.Equal(100, point.Time);
        Assert.False(buffer.TryGetNearest(1001, 1001, 1100, out _));
        Assert.False(buffer.TryGetVisibleRange(1001, 1100, out _, out _));
    }

    [Theory]
    [InlineData(129, 100)]
    [InlineData(129, 600)]
    [InlineData(4096, 3100)]
    public void SnapshotCopiesBothPhysicalRunsInChronologicalOrder(int capacity, int added)
    {
        var buffer = new ChannelBuffer(capacity);
        for (var i = 0; i < added; i++) buffer.Add(i / 2000.0, i);
        var first = Math.Max(0, added - capacity);

        Assert.Equal(Enumerable.Range(first, Math.Min(capacity, added))
            .Select(i => new SamplePoint(i / 2000.0, i)), buffer.ToArray());
    }
}
