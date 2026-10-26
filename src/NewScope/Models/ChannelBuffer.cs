namespace NewScope.Models;

public readonly record struct SamplePoint(double Time, double Value)
{
    public bool IsValid => !double.IsNaN(Value) && !double.IsInfinity(Value);
}

public readonly record struct ChannelStats(double Min, double Max, double Average, double Last, int Count);

/// <summary>
/// A fixed-capacity, monotonically-timed sample ring. One writer (the
/// acquisition loop) and one reader (the UI) are supported through a small lock.
/// </summary>
public sealed class ChannelBuffer
{
    private const int InitialCapacity = 2_048;
    private const double MaxRetentionSeconds = 60;
    private const int SummaryBlockSize = 64;
    // A 48-byte summary per 64 samples, rounded up for memory budgeting.
    internal const int SummaryBytesPerSample = 1;
    private readonly object _gate = new();
    private int _maxCapacity;
    private double[] _times;
    private double[] _values;
    private RangeSummary[] _summaries;
    private int _head;
    private int _count;

    public ChannelBuffer(int capacity = 200_000)
    {
        if (capacity < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _maxCapacity = capacity;
        var initial = Math.Min(capacity, InitialCapacity);
        _times = new double[initial];
        _values = new double[initial];
        _summaries = new RangeSummary[(initial + SummaryBlockSize - 1) / SummaryBlockSize];
    }

    public int Capacity => _times.Length;

    public int MaxCapacity
    {
        get
        {
            lock (_gate)
            {
                return _maxCapacity;
            }
        }
    }

    public void SetMaxCapacity(int capacity)
    {
        if (capacity < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        lock (_gate)
        {
            _maxCapacity = capacity;
            if (_times.Length <= capacity)
            {
                return;
            }

            var keep = Math.Min(_count, capacity);
            var first = _count - keep;
            var times = new double[capacity];
            var values = new double[capacity];
            for (var i = 0; i < keep; i++)
            {
                var point = PointAt(first + i);
                times[i] = point.Time;
                values[i] = point.Value;
            }

            _times = times;
            _values = values;
            _head = keep % capacity;
            _count = keep;
            RebuildSummaries();
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public void Add(double time, double value)
    {
        if (!double.IsFinite(time))
        {
            throw new ArgumentOutOfRangeException(nameof(time));
        }

        lock (_gate)
        {
            if (_count == _times.Length && _times.Length < _maxCapacity &&
                time - _times[PhysicalIndex(0)] < MaxRetentionSeconds)
            {
                Grow();
            }

            var writtenIndex = _head;
            _times[_head] = time;
            _values[_head] = value;
            _head = (_head + 1) % _times.Length;
            if (_count < _times.Length)
            {
                _count++;
            }

            RecomputeSummary(writtenIndex / SummaryBlockSize);
        }
    }

    private void Grow()
    {
        var length = Math.Min(_maxCapacity, Math.Max(4, _times.Length * 2));
        if (length <= _times.Length)
        {
            return;
        }

        var times = new double[length];
        var values = new double[length];
        var start = PhysicalIndex(0);
        for (var i = 0; i < _count; i++)
        {
            var index = (start + i) % _times.Length;
            times[i] = _times[index];
            values[i] = _values[index];
        }

        _times = times;
        _values = values;
        _head = _count;
        RebuildSummaries();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _head = 0;
            _count = 0;
            Array.Clear(_summaries);
        }
    }

    public ChannelStats GetStats()
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                return new ChannelStats(0, 0, 0, 0, 0);
            }

            var summary = Summarize(0, _count);
            if (summary.FiniteCount == 0)
            {
                return new ChannelStats(0, 0, 0, 0, _count);
            }

            return new ChannelStats(
                summary.Min, summary.Max, summary.Sum / summary.FiniteCount, summary.Last, _count);
        }
    }

    public bool TryGetLatest(out SamplePoint point)
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                point = default;
                return false;
            }

            var index = (_head - 1 + _times.Length) % _times.Length;
            point = new SamplePoint(_times[index], _values[index]);
            return true;
        }
    }

    public (double First, double Last) GetTimeRange()
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                return (0, 0);
            }

            return (_times[PhysicalIndex(0)], _times[PhysicalIndex(_count - 1)]);
        }
    }

    /// <summary>
    /// Copies at most <paramref name="maxPoints"/> visible points into
    /// <paramref name="destination"/>. The result includes the newest sample
    /// and (with a budget of at least four) chronological min/max pairs for
    /// each time bucket, preserving spikes without exceeding the point budget.
    /// </summary>
    public int CopyVisible(double startTime, double endTime, Span<SamplePoint> destination, int maxPoints)
    {
        if (maxPoints <= 0 || destination.Length == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            if (_count == 0 || endTime < startTime)
            {
                return 0;
            }

            var first = LowerBound(startTime);
            if (first >= _count)
            {
                return 0;
            }

            var afterLast = UpperBound(endTime);
            if (afterLast <= first)
            {
                return 0;
            }

            var total = afterLast - first;
            var limit = Math.Min(maxPoints, destination.Length);
            if (total <= limit)
            {
                for (var i = 0; i < total; i++)
                {
                    destination[i] = PointAt(first + i);
                }

                return total;
            }

            if (limit == 1)
            {
                destination[0] = PointAt(afterLast - 1);
                return 1;
            }

            destination[0] = PointAt(first);
            var lastPoint = PointAt(afterLast - 1);
            var written = 1;
            if (limit == 3)
            {
                var summary = Summarize(first + 1, afterLast - 1);
                if (summary.FiniteCount > 0)
                {
                    var midpoint = (destination[0].Value + lastPoint.Value) / 2;
                    var index = Math.Abs(summary.Min - midpoint) > Math.Abs(summary.Max - midpoint)
                        ? summary.MinIndex
                        : summary.MaxIndex;
                    destination[written++] = new SamplePoint(_times[index], _values[index]);
                }
            }
            else if (limit >= 4)
            {
                var bucketCount = (limit - 2) / 2;
                var bucketStart = first + 1;
                var span = lastPoint.Time - destination[0].Time;
                for (var bucket = 0; bucket < bucketCount; bucket++)
                {
                    var bucketEnd = bucket == bucketCount - 1
                        ? afterLast - 1
                        : Math.Clamp(
                            LowerBound(destination[0].Time + span * (bucket + 1) / bucketCount),
                            bucketStart, afterLast - 1);
                    var summary = Summarize(bucketStart, bucketEnd);
                    if (summary.FiniteCount > 0)
                    {
                        var min = LogicalIndex(summary.MinIndex);
                        var max = LogicalIndex(summary.MaxIndex);
                        destination[written++] = PointAt(Math.Min(min, max));
                        if (min != max)
                        {
                            destination[written++] = PointAt(Math.Max(min, max));
                        }
                    }
                    else if (bucketEnd > bucketStart)
                    {
                        destination[written++] = PointAt(bucketStart);
                    }

                    bucketStart = bucketEnd;
                }
            }

            destination[written++] = lastPoint;
            return written;
        }
    }


    public bool TryGetValueAtTime(double time, out double value)
    {
        lock (_gate)
        {
            var index = LowerBound(time);
            if (index < _count && Math.Abs(PointAt(index).Time - time) <= 1e-9)
            {
                value = PointAt(index).Value;
                return true;
            }
        }

        value = 0;
        return false;
    }

    public bool TryGetVisibleRange(double startTime, double endTime, out double min, out double max)
    {
        lock (_gate)
        {
            if (_count > 0 && !double.IsNaN(startTime) && !double.IsNaN(endTime) && endTime >= startTime)
            {
                var summary = Summarize(LowerBound(startTime), UpperBound(endTime));
                if (summary.FiniteCount > 0)
                {
                    min = summary.Min;
                    max = summary.Max;
                    return true;
                }
            }
        }

        min = max = 0;
        return false;
    }

    public bool TryGetNearest(double time, double startTime, double endTime, out SamplePoint point)
    {
        lock (_gate)
        {
            if (_count > 0 && double.IsFinite(time) && !double.IsNaN(startTime) &&
                !double.IsNaN(endTime) && endTime >= startTime)
            {
                var first = LowerBound(startTime);
                var afterLast = UpperBound(endTime);
                if (first < afterLast)
                {
                    var index = Math.Clamp(LowerBound(time), first, afterLast - 1);
                    if (index > first && Math.Abs(PointAt(index - 1).Time - time) <= Math.Abs(PointAt(index).Time - time))
                    {
                        index--;
                    }

                    point = PointAt(index);
                    return true;
                }
            }
        }

        point = default;
        return false;
    }

    public SamplePoint[] ToArray()
    {
        lock (_gate)
        {
            var result = new SamplePoint[_count];
            var start = PhysicalIndex(0);
            var firstLength = Math.Min(_count, _times.Length - start);
            CopyContiguous(start, result.AsSpan(0, firstLength));
            CopyContiguous(0, result.AsSpan(firstLength));

            return result;
        }
    }

    private void CopyContiguous(int start, Span<SamplePoint> destination)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = new SamplePoint(_times[start + i], _values[start + i]);
        }
    }

    private int PhysicalIndex(int logicalIndex) => (_head - _count + logicalIndex + _times.Length * 2) % _times.Length;

    private SamplePoint PointAt(int logicalIndex)
    {
        var index = PhysicalIndex(logicalIndex);
        return new SamplePoint(_times[index], _values[index]);
    }

    private int LogicalIndex(int physicalIndex) =>
        (physicalIndex - PhysicalIndex(0) + _times.Length) % _times.Length;

    private void RebuildSummaries()
    {
        _summaries = new RangeSummary[(_times.Length + SummaryBlockSize - 1) / SummaryBlockSize];
        for (var block = 0; block < _summaries.Length; block++)
        {
            RecomputeSummary(block);
        }
    }

    private void RecomputeSummary(int block)
    {
        var summary = new RangeSummary();
        var start = block * SummaryBlockSize;
        var end = Math.Min(start + SummaryBlockSize, _count);
        for (var i = start; i < end; i++)
        {
            summary.Include(_values[i], i);
        }

        _summaries[block] = summary;
    }

    private RangeSummary Summarize(int first, int afterLast)
    {
        if (first >= afterLast)
        {
            return default;
        }

        var start = PhysicalIndex(first);
        var length = afterLast - first;
        var firstLength = Math.Min(length, _times.Length - start);
        var summary = SummarizePhysical(start, start + firstLength);
        if (firstLength < length)
        {
            summary.Append(SummarizePhysical(0, length - firstLength));
        }

        return summary;
    }

    private RangeSummary SummarizePhysical(int start, int end)
    {
        var summary = new RangeSummary();
        while (start < end)
        {
            if (start % SummaryBlockSize == 0 && start + SummaryBlockSize <= end)
            {
                summary.Append(_summaries[start / SummaryBlockSize]);
                start += SummaryBlockSize;
            }
            else
            {
                summary.Include(_values[start], start);
                start++;
            }
        }

        return summary;
    }

    private struct RangeSummary
    {
        public int FiniteCount;
        public double Sum;
        public double Min;
        public double Max;
        public double Last;
        public int MinIndex;
        public int MaxIndex;

        public void Include(double value, int index)
        {
            if (!double.IsFinite(value))
            {
                return;
            }

            if (FiniteCount == 0 || value < Min)
            {
                Min = value;
                MinIndex = index;
            }

            if (FiniteCount == 0 || value > Max)
            {
                Max = value;
                MaxIndex = index;
            }

            FiniteCount++;
            Sum += value;
            Last = value;
        }

        public void Append(RangeSummary other)
        {
            if (other.FiniteCount == 0)
            {
                return;
            }

            if (FiniteCount == 0)
            {
                this = other;
                return;
            }

            if (other.Min < Min)
            {
                Min = other.Min;
                MinIndex = other.MinIndex;
            }

            if (other.Max > Max)
            {
                Max = other.Max;
                MaxIndex = other.MaxIndex;
            }

            Sum += other.Sum;
            FiniteCount += other.FiniteCount;
            Last = other.Last;
        }
    }

    private int LowerBound(double time)
    {
        var low = 0;
        var high = _count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (PointAt(mid).Time < time)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private int UpperBound(double time)
    {
        var low = 0;
        var high = _count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (PointAt(mid).Time <= time)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }
}
