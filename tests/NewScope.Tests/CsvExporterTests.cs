using System.Globalization;
using NewScope.Models;
using NewScope.Services;
using NewScope.ViewModels;

namespace NewScope.Tests;

public class CsvExporterTests
{
    [Fact]
    public void WritesHeaderAndRowsWithMissingValuesLeftEmpty()
    {
        var first = CreateChannel("alpha");
        var second = CreateChannel("beta");
        first.Buffer.Add(0.1, 1.5);
        first.Buffer.Add(0.2, 2.5);
        second.Buffer.Add(0.1, 10);

        var csv = CsvExporter.Build([first, second]);
        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("time_s,alpha,beta", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Equal("0.100000,1.5,10", lines[1]);
        Assert.Equal("0.200000,2.5,", lines[2]);
    }

    [Fact]
    public void EscapesNamesContainingCommasAndQuotes()
    {
        var channel = CreateChannel("value,\"x\"");
        channel.Buffer.Add(1, 3);

        var lines = CsvExporter.Build([channel])
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("time_s,\"value,\"\"x\"\"\"", lines[0]);
        Assert.Equal("1.000000,3", lines[1]);
    }

    [Fact]
    public void ReportsProgressAndHonorsCancellation()
    {
        var channel = CreateChannel("large");
        for (var i = 0; i < 1_000; i++)
        {
            channel.Buffer.Add(i / 100.0, i);
        }

        var reports = new List<double>();
        var progress = new SynchronousProgress(reports.Add);
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var rows = CsvExporter.Write(writer, [channel], progress);

        Assert.Equal(1_000, rows);
        Assert.Equal(1, reports[^1]);
        Assert.True(reports.Count > 2);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var cancelledWriter = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Throws<OperationCanceledException>(() =>
            CsvExporter.Write(cancelledWriter, [channel], cancellationToken: cancelled.Token));
    }

    [Fact]
    public void AllChannelsAreCapturedBeforeTheFirstOutputIsWritten()
    {
        var first = CreateChannel("alpha");
        var second = CreateChannel("beta");
        first.Buffer.Add(1, 10);
        second.Buffer.Add(1, 100);
        using var writer = new AdvancingWriter(() =>
        {
            first.Buffer.Clear();
            second.Buffer.Clear();
            first.Buffer.Add(2, 20);
            second.Buffer.Add(2, 200);
        });

        CsvExporter.Write(writer, [first, second]);

        Assert.Contains("1.000000,10,100", writer.ToString());
        Assert.DoesNotContain("2.000000", writer.ToString());
    }

    [Fact]
    public void ExportsTheUnionOfChannelTimelines()
    {
        var first = CreateChannel("alpha");
        var second = CreateChannel("beta");
        first.Buffer.Add(0, 10);
        first.Buffer.Add(1, 11);
        second.Buffer.Add(1, 101);
        second.Buffer.Add(2, 102);

        var lines = CsvExporter.Build([first, second])
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(["time_s,alpha,beta", "0.000000,10,", "1.000000,11,101", "2.000000,,102"], lines);
    }

    [Fact]
    public async Task CancellingAnOverwriteKeepsTheOriginalFileAndRemovesTheTemporaryFile()
    {
        using var directory = new TestDirectory();
        var path = directory.File("capture.csv");
        await File.WriteAllTextAsync(path, "original capture");
        using var cts = new CancellationTokenSource();
        var progress = new SynchronousProgress(value =>
        {
            if (value > 0)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CsvExporter.ExportSnapshotAsync(path, CreateLargeSnapshot(), progress, cts.Token));

        Assert.Equal("original capture", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task FailedExportKeepsTheOriginalFile()
    {
        using var directory = new TestDirectory();
        var path = directory.File("capture.csv");
        await File.WriteAllTextAsync(path, "original capture");
        var progress = new SynchronousProgress(_ => throw new IOException("simulated write failure"));

        await Assert.ThrowsAsync<IOException>(() =>
            CsvExporter.ExportSnapshotAsync(path, CreateLargeSnapshot(), progress));

        Assert.Equal("original capture", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task SuccessfulExportReplacesTheFileOnlyAfterAllRowsAreWritten()
    {
        using var directory = new TestDirectory();
        var path = directory.File("capture.csv");
        await File.WriteAllTextAsync(path, "old capture");
        ChannelSnapshot[] snapshot = [new("signal", [new SamplePoint(1, 42)])];

        var rows = await CsvExporter.ExportSnapshotAsync(path, snapshot);

        Assert.Equal(1, rows);
        Assert.Equal(CsvExporter.BuildSnapshot(snapshot), await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task CancelledExportToANewPathLeavesNoPartialFile()
    {
        using var directory = new TestDirectory();
        var path = directory.File("capture.csv");
        using var cts = new CancellationTokenSource();
        var progress = new SynchronousProgress(value =>
        {
            if (value > 0)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CsvExporter.ExportSnapshotAsync(path, CreateLargeSnapshot(), progress, cts.Token));

        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void SameNameVariablesHaveUnambiguousCsvHeaders()
    {
        var first = CreateChannel("counter");
        var second = CreateChannel("counter");
        second.Symbol.Address += 4;
        first.Buffer.Add(0, 1);
        second.Buffer.Add(0, 2);

        Assert.StartsWith("time_s,counter@0x20000000,counter@0x20000004", CsvExporter.Build([first, second]));
    }

    private static ChannelSnapshot[] CreateLargeSnapshot() =>
        [new("signal", Enumerable.Range(0, 1000).Select(i => new SamplePoint(i, i)).ToArray())];

    private static ChannelViewModel CreateChannel(string name) => new(
        new SymbolVariable
        {
            Name = name,
            Address = 0x2000_0000,
            Size = 4,
            Type = "int",
            Kind = "int",
            Signed = true
        },
        0);

    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class AdvancingWriter(Action advance) : StringWriter(CultureInfo.InvariantCulture)
    {
        private bool _advanced;

        public override void WriteLine()
        {
            base.WriteLine();
            if (!_advanced)
            {
                _advanced = true;
                advance();
            }
        }
    }
}
