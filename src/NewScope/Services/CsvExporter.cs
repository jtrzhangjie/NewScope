using System.Globalization;
using System.Text;
using NewScope.Models;

namespace NewScope.Services;

public static class CsvExporter
{
    public static ChannelSnapshot[] Capture(
        IReadOnlyList<ISampleChannel> channels,
        CancellationToken cancellationToken = default)
    {
        var duplicateNames = channels.GroupBy(channel => channel.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var snapshots = new ChannelSnapshot[channels.Count];
        for (var i = 0; i < channels.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channel = channels[i];
            var name = duplicateNames.Contains(channel.Name)
                ? $"{channel.Name}@{channel.Symbol.AddressText}"
                : channel.Name;
            snapshots[i] = new ChannelSnapshot(name, channel.Buffer.ToArray());
        }

        return snapshots;
    }

    public static int Write(
        TextWriter writer,
        IReadOnlyList<ISampleChannel> channels,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        WriteSnapshot(writer, Capture(channels, cancellationToken), progress, cancellationToken);

    public static int WriteSnapshot(
        TextWriter writer,
        IReadOnlyList<ChannelSnapshot> channels,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (channels.Count == 0)
        {
            return 0;
        }

        writer.Write("time_s");
        foreach (var channel in channels)
        {
            writer.Write(',');
            writer.Write(Escape(channel.Name));
        }

        writer.WriteLine();

        var positions = new int[channels.Count];
        var totalSamples = channels.Sum(channel => (long)channel.Samples.Length);
        long consumed = 0;
        var rows = 0;
        var lastReported = -1;
        while (true)
        {
            if ((rows & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var percent = totalSamples == 0 ? 100 : (int)(consumed * 100 / totalSamples);
                if (percent != lastReported)
                {
                    progress?.Report(percent / 100.0);
                    lastReported = percent;
                }
            }

            // Merge all channel timelines, including channels selected at
            // different times. A single "longest" channel can omit valid rows.
            var time = double.PositiveInfinity;
            for (var i = 0; i < channels.Count; i++)
            {
                var samples = channels[i].Samples;
                if (positions[i] < samples.Length)
                {
                    time = Math.Min(time, samples[positions[i]].Time);
                }
            }

            if (!double.IsFinite(time))
            {
                break;
            }

            writer.Write(time.ToString("0.000000", CultureInfo.InvariantCulture));
            for (var i = 0; i < channels.Count; i++)
            {
                writer.Write(',');
                var samples = channels[i].Samples;
                if (positions[i] < samples.Length && Math.Abs(samples[positions[i]].Time - time) <= 1e-9)
                {
                    var value = samples[positions[i]++].Value;
                    consumed++;
                    if (double.IsFinite(value))
                    {
                        writer.Write(value.ToString("G17", CultureInfo.InvariantCulture));
                    }
                }
            }

            writer.WriteLine();
            rows++;
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(1);
        return rows;
    }

    public static string Build(IReadOnlyList<ISampleChannel> channels) => BuildSnapshot(Capture(channels));

    public static string BuildSnapshot(IReadOnlyList<ChannelSnapshot> channels)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        WriteSnapshot(writer, channels);
        return writer.ToString();
    }

    public static Task<int> ExportSnapshotAsync(
        string path,
        IReadOnlyList<ChannelSnapshot> channels,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var rows = 0;
        AtomicFile.Write(path, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
            rows = WriteSnapshot(writer, channels, progress, cancellationToken);
        }, cancellationToken);
        return rows;
    }, cancellationToken);

    private static string Escape(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
