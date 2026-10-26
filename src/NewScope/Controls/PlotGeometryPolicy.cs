using Avalonia;

namespace NewScope.Controls;

internal static class PlotGeometryPolicy
{
    /// <summary>
    /// Simplifies a monotonically increasing-X screen polyline in place.
    /// The slope corridor bounds the vertical error to a quarter logical pixel;
    /// this is a linear pass with no allocations and retains vertical segments.
    /// Raw samples, extrema and cursor measurements are not modified.
    /// </summary>
    public static int Simplify(Span<Point> points, double tolerance = 0.25)
    {
        if (points.Length < 3)
        {
            return points.Length;
        }

        var written = 1;
        var anchor = points[0];
        var previous = anchor;
        var lowerSlope = double.NegativeInfinity;
        var upperSlope = double.PositiveInfinity;
        for (var i = 1; i < points.Length; i++)
        {
            var current = points[i];
            var dx = current.X - anchor.X;
            if (dx <= 0)
            {
                if (points[written - 1] != previous) points[written++] = previous;
                if (points[written - 1] != current) points[written++] = current;
                anchor = previous = current;
                lowerSlope = double.NegativeInfinity;
                upperSlope = double.PositiveInfinity;
                continue;
            }

            var slope = (current.Y - anchor.Y) / dx;
            if (slope < lowerSlope || slope > upperSlope)
            {
                if (points[written - 1] != previous) points[written++] = previous;
                anchor = previous;
                lowerSlope = double.NegativeInfinity;
                upperSlope = double.PositiveInfinity;
                dx = current.X - anchor.X;
                if (dx <= 0)
                {
                    points[written++] = current;
                    anchor = previous = current;
                    continue;
                }
            }

            lowerSlope = Math.Max(lowerSlope, (current.Y - anchor.Y - tolerance) / dx);
            upperSlope = Math.Min(upperSlope, (current.Y - anchor.Y + tolerance) / dx);
            previous = current;
        }

        if (points[written - 1] != previous) points[written++] = previous;
        return written;
    }
}
