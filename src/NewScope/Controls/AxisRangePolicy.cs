namespace NewScope.Controls;

public sealed class AxisRangePolicy
{
    private const double ShrinkRatio = 0.6;
    private const long ShrinkDelayMilliseconds = 2000;
    private double _min = double.NaN;
    private double _max = double.NaN;
    private long _narrowSince;

    public (double Min, double Max) Update(double dataMin, double dataMax, long now)
    {
        if (!double.IsFinite(dataMin) || !double.IsFinite(dataMax))
        {
            return (-1, 1);
        }

        if (Math.Abs(dataMax - dataMin) < 1e-9)
        {
            var padding = Math.Max(1, Math.Abs(dataMax) * 0.1);
            dataMin = ClampFinite(dataMin - padding);
            dataMax = ClampFinite(dataMax + padding);
        }
        else
        {
            var padding = (dataMax - dataMin) * 0.08;
            if (!double.IsFinite(padding))
            {
                padding = dataMax * 0.08 - dataMin * 0.08;
            }
            dataMin = ClampFinite(dataMin - padding);
            dataMax = ClampFinite(dataMax + padding);
        }

        if (dataMin > 0 && dataMin < (dataMax - dataMin) * 0.35)
        {
            dataMin = 0;
        }
        else if (dataMax < 0 && dataMax > -(dataMax - dataMin) * 0.35)
        {
            dataMax = 0;
        }

        var target = NiceRange(dataMin, dataMax, 4);
        if (!double.IsFinite(_min) || !double.IsFinite(_max) || _max <= _min)
        {
            Adopt(target);
            return target;
        }

        var current = (_min, _max);
        var currentSpan = _max - _min;
        var targetSpan = target.Max - target.Min;

        if (target.Min < _min || target.Max > _max)
        {
            Adopt(target);
            return target;
        }

        if (targetSpan >= currentSpan * ShrinkRatio)
        {
            _narrowSince = 0;
            return current;
        }

        if (_narrowSince == 0)
        {
            _narrowSince = now;
            return current;
        }

        if (now - _narrowSince < ShrinkDelayMilliseconds)
        {
            return current;
        }

        Adopt(target);
        return target;
    }

    public void Reset()
    {
        _min = double.NaN;
        _max = double.NaN;
        _narrowSince = 0;
    }

    public static (double Min, double Max) NiceRange(double min, double max, int tickCount)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || max <= min || tickCount <= 0)
        {
            return (-1, 1);
        }

        var rawStep = (max - min) / tickCount;
        if (!double.IsFinite(rawStep))
        {
            rawStep = max / tickCount - min / tickCount;
        }
        if (rawStep <= 0 || !double.IsFinite(rawStep))
        {
            return (min, max);
        }
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
        if (magnitude == 0)
        {
            return (min, max);
        }
        var residual = rawStep / magnitude;
        var niceResidual = residual switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 5 => 5,
            _ => 10
        };
        var step = niceResidual * magnitude;
        var lower = ClampFinite(Math.Floor(min / step) * step);
        var upper = ClampFinite(Math.Ceiling(max / step) * step);
        return upper > lower ? (lower, upper) : (min, max);
    }

    private static double ClampFinite(double value) => Math.Clamp(value, -double.MaxValue, double.MaxValue);

    private void Adopt((double Min, double Max) range)
    {
        _min = range.Min;
        _max = range.Max;
        _narrowSince = 0;
    }
}
