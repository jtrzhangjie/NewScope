using Avalonia;
using NewScope.Controls;

namespace NewScope.Tests;

public class PlotGeometryPolicyTests
{
    [Fact]
    public void AFlatOrStraightTraceNeedsOnlyItsEndpoints()
    {
        var points = Enumerable.Range(0, 1000).Select(i => new Point(i, i * 2)).ToArray();

        var count = PlotGeometryPolicy.Simplify(points);

        Assert.Equal(2, count);
        Assert.Equal(new Point(0, 0), points[0]);
        Assert.Equal(new Point(999, 1998), points[1]);
    }

    [Fact]
    public void SimplifiedTraceStaysWithinSubpixelErrorAndPreservesSharpSpikes()
    {
        var original = Enumerable.Range(0, 2000)
            .Select(i => new Point(i * 0.5, i == 751 ? 1000 : i == 752 ? -1000 : Math.Sin(i * 0.05) * 10))
            .ToArray();
        var simplified = original.ToArray();

        var count = PlotGeometryPolicy.Simplify(simplified);

        Assert.True(count < original.Length / 2);
        Assert.Contains(new Point(751 * 0.5, 1000), simplified.Take(count));
        Assert.Contains(new Point(752 * 0.5, -1000), simplified.Take(count));
        var segment = 0;
        foreach (var point in original)
        {
            while (segment + 1 < count - 1 && simplified[segment + 1].X < point.X) segment++;
            var first = simplified[segment];
            var last = simplified[segment + 1];
            var y = first.Y + (last.Y - first.Y) * (point.X - first.X) / (last.X - first.X);
            Assert.InRange(Math.Abs(point.Y - y), 0, 0.250000001);
        }
    }

    [Fact]
    public void EqualTimeVerticalSegmentsAreRetained()
    {
        Point[] points = [new(0, 0), new(1, 0), new(1, 100), new(1, -100), new(2, 0), new(3, 0)];

        var count = PlotGeometryPolicy.Simplify(points);

        Assert.Contains(new Point(1, 100), points.Take(count));
        Assert.Contains(new Point(1, -100), points.Take(count));
        Assert.Equal(new Point(3, 0), points[count - 1]);
    }
}
