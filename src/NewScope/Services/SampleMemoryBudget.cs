using NewScope.Models;

namespace NewScope.Services;

public static class SampleMemoryBudget
{
    public const long MaxBufferBytes = 512L * 1024 * 1024;
    public const int DefaultChannelCapacity = 200_000;
    public const int MinimumChannelCapacity = 2_048;
    private const int BytesPerSample = sizeof(double) * 2 + ChannelBuffer.SummaryBytesPerSample;
    private const double RetentionSeconds = 60;

    public static int GetCapacity(int channelCount, int sampleRateHz)
    {
        if (channelCount <= 0 || sampleRateHz <= 0)
        {
            return DefaultChannelCapacity;
        }

        var perChannelBytes = MaxBufferBytes / channelCount;
        var budgetCapacity = perChannelBytes / BytesPerSample;
        var retentionCapacity = (long)Math.Ceiling(sampleRateHz * RetentionSeconds);
        var capacity = Math.Min(DefaultChannelCapacity, Math.Min(retentionCapacity, budgetCapacity));
        return (int)Math.Max(MinimumChannelCapacity, capacity);
    }

    public static bool IsConstrained(int channelCount, int sampleRateHz)
    {
        if (channelCount <= 0 || sampleRateHz <= 0)
        {
            return false;
        }

        var requested = Math.Min(DefaultChannelCapacity, (long)Math.Ceiling(sampleRateHz * RetentionSeconds));
        return GetCapacity(channelCount, sampleRateHz) < requested;
    }

    public static double Retention(ChannelBuffer buffer, int sampleRateHz) =>
        sampleRateHz <= 0 ? 0 : buffer.MaxCapacity / (double)sampleRateHz;
}
