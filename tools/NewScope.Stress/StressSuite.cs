using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NewScope.Controls;
using NewScope.Models;
using NewScope.Services;
using NewScope.ViewModels;
using NewScope.Views;
using Pointer = Avalonia.Input.Pointer;

namespace NewScope.Stress;

internal sealed partial class StressSuite
{
    private static readonly Pointer TestPointer = new(991, PointerType.Mouse, true);
    private static readonly PointerPointProperties NoButtons = new();
    private static readonly PointerPointProperties LeftDown = new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
    private static readonly Dictionary<WaveformPlot, Window> TestHosts = [];
    private static readonly Dictionary<string, MethodInfo> InputMethods = new[]
        { "OnPointerWheelChanged", "OnPointerMoved", "OnPointerPressed", "OnPointerReleased", "OnPointerExited" }
        .ToDictionary(name => name, name => typeof(WaveformPlot).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!);

    private void RunEdgeCases()
    {
        foreach (var (name, values) in new (string, double[])[]
                 {
                     ("finite", [1, -1, 3]), ("nonfinite-first", [double.NaN, 1, double.PositiveInfinity, -2]),
                     ("all-nonfinite", [double.NaN, double.NegativeInfinity]),
                     ("float-extremes", [float.MinValue, float.MaxValue]),
                     ("double-extremes", [-double.MaxValue, double.MaxValue])
                 })
        {
            foreach (var width in new[] { 80, 100, 120, 320, 900 })
            {
                var channel = Channel(0, 2048);
                for (var i = 0; i < values.Length; i++) channel.Buffer.Add(i, values[i]);
                var plot = new WaveformPlot { Channels = new[] { channel } };
                try
                {
                    Arrange(plot, width, 180);
                    using var target = new RenderTargetBitmap(new PixelSize(width, 180));
                    target.Render(plot);
                    Move(plot, Center(plot));
                    target.Render(plot);
                    Wheel(plot, 1, yAxis: true);
                    target.Render(plot);
                }
                catch (Exception exception)
                {
                    var error = $"edge/{name}/{width}: {exception.GetBaseException()}";
                    report.Errors.Add(error);
                    Console.WriteLine($"edge/{name}/{width}: {exception.GetBaseException().Message}");
                }
                finally
                {
                    plot.Channels = null;
                    CloseHost(plot);
                }
            }
        }

        var zeroPlot = new WaveformPlot { Channels = new[] { Channel(0, 2048) } };
        Arrange(zeroPlot, 900, 300);
        using (var target = new RenderTargetBitmap(new PixelSize(900, 300))) target.Render(zeroPlot);
        var initialSpan = zeroPlot.ViewSpanText;
        var (root, position) = InputPosition(zeroPlot, Center(zeroPlot));
        var check = new PointerWheelEventArgs(zeroPlot, TestPointer, root, position, 0, NoButtons, KeyModifiers.None, new Vector(0, 1));
        Console.WriteLine($"Input fixture: expected={Center(zeroPlot)}, actual={check.GetPosition(zeroPlot)}");
        var difference = check.GetPosition(zeroPlot) - Center(zeroPlot);
        if (Math.Abs(difference.X) + Math.Abs(difference.Y) > 0.01)
            throw new InvalidOperationException("Input fixture could not resolve plot coordinates.");
        Wheel(zeroPlot, 0);
        if (zeroPlot.ViewSpanText != initialSpan)
            report.Errors.Add($"Zero wheel delta changed the time span: {initialSpan} -> {zeroPlot.ViewSpanText}");
        for (var i = 0; i < 1000; i++) Wheel(zeroPlot, 1);
        if (Math.Abs((double)Field(zeroPlot, "_followSpan")! - 0.01) > 1e-9)
            report.Errors.Add("Time zoom did not stop at its 10 ms lower limit.");
        for (var i = 0; i < 1000; i++) Wheel(zeroPlot, -1);
        if (Math.Abs((double)Field(zeroPlot, "_followSpan")! - 3600) > 1e-9)
            report.Errors.Add("Time zoom did not stop at its one-hour upper limit.");
        for (var i = 0; i < 1000; i++) Wheel(zeroPlot, 1, yAxis: true);
        for (var i = 0; i < 1000; i++) Wheel(zeroPlot, -1, yAxis: true);
        zeroPlot.FitAll();
        var input = InputPosition(zeroPlot, Center(zeroPlot));
        InputMethods["OnPointerPressed"].Invoke(zeroPlot,
            [new PointerPressedEventArgs(zeroPlot, TestPointer, input.Root, input.Position, 0, LeftDown, KeyModifiers.None, 1)]);
        TestPointer.Capture(null);
        if ((bool)Field(zeroPlot, "_isDragging")!) report.Errors.Add("Lost mouse capture left the plot in dragging mode.");
        zeroPlot.Channels = null;
        CloseHost(zeroPlot);
    }

    private async Task RunHistoryCaseAsync(int count)
    {
        Console.WriteLine($"Preparing {count} channels with saturated 2000 Hz history...");
        var capacity = SampleMemoryBudget.GetCapacity(count, 2000);
        var channels = Enumerable.Range(0, count).Select(index => Channel(index, capacity)).ToArray();
        var fill = Stopwatch.StartNew();
        await Task.Run(() => Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, channel =>
        {
            for (var i = 0; i < capacity; i++) channels[channel].Buffer.Add(i / 2000.0, Value(i, channel));
        }));
        fill.Stop();
        Collect();
        var memory = Memory();
        var stats = Timings.Measure(20, () =>
        {
            foreach (var channel in channels) _ = channel.Buffer.GetStats();
        });
        foreach (var panes in new[] { 1, 4 })
        {
            var plots = Enumerable.Range(0, panes).Select(pane => new WaveformPlot
            {
                Channels = channels.Where((_, index) => index % panes == pane).ToArray(),
                AutoFollow = false
            }).ToArray();
            var targets = plots.Select(_ => new RenderTargetBitmap(new PixelSize(900, panes == 1 ? 500 : 180))).ToArray();
            var frames = new List<double>();
            var interactions = new List<double>();
            try
            {
                for (var i = 0; i < plots.Length; i++)
                {
                    Arrange(plots[i], 900, panes == 1 ? 500 : 180);
                    targets[i].Render(plots[i]);
                }
                for (var frame = 0; frame < options.Frames; frame++)
                {
                    var interaction = Stopwatch.StartNew();
                    foreach (var plot in plots)
                    {
                        plot.FitAll();
                        for (var step = 0; step < frame % 45; step++) Wheel(plot, 1);
                        if (frame % 3 == 0) Drag(plot, new Vector(-100, 0));
                        if (frame % 4 == 0) Wheel(plot, frame % 8 == 0 ? 1 : -1, yAxis: true);
                        if (frame % 2 == 0) Move(plot, Center(plot));
                        else Exit(plot);
                        AssertViewport(plot);
                    }
                    interactions.Add(interaction.Elapsed.TotalMilliseconds);
                    var render = Stopwatch.StartNew();
                    for (var i = 0; i < plots.Length; i++) targets[i].Render(plots[i]);
                    frames.Add(render.Elapsed.TotalMilliseconds);
                    if ((frame & 7) == 0) await Task.Delay(1);
                }

                var result = new
                {
                    Kind = "history-render", Channels = count, Panes = panes, SamplesPerChannel = capacity,
                    TotalSamples = (long)capacity * count, RetentionSeconds = capacity / 2000.0,
                    FillSeconds = fill.Elapsed.TotalSeconds, Memory = memory, StatsAllChannels = stats,
                    Render = Timings.From(frames), Interaction = Timings.From(interactions)
                };
                report.Results.Add(result);
                Console.WriteLine($"{count} ch / {panes} panes: render {result.Render}; interaction {result.Interaction}; heap={memory.ManagedMiB:F1} MiB, private={memory.PrivateMiB:F1} MiB");
                if (count == 256 && panes == 1)
                    targets[0].Save(Path.ChangeExtension(options.Output, ".waveform.png"), PngBitmapEncoderOptions.Default);
            }
            catch (Exception exception)
            {
                report.Errors.Add($"history/{count}/{panes}: {exception}");
            }
            finally
            {
                for (var i = 0; i < plots.Length; i++)
                {
                    plots[i].Channels = null;
                    targets[i].Dispose();
                    CloseHost(plots[i]);
                }
            }
        }

        var snapshotWatch = Stopwatch.StartNew();
        var snapshot = await Task.Run(() => CsvExporter.Capture(channels));
        snapshotWatch.Stop();
        var snapshotMemory = Memory();
        using var cancelled = new CancellationTokenSource();
        var progress = new InlineProgress(value => { if (value > 0) cancelled.Cancel(); });
        var exportPath = Path.ChangeExtension(options.Output, $".{count}.cancel.csv");
        await File.WriteAllTextAsync(exportPath, "original-file-must-survive");
        var cancelWatch = Stopwatch.StartNew();
        try
        {
            await CsvExporter.ExportSnapshotAsync(exportPath, snapshot, progress, cancelled.Token);
            report.Errors.Add($"CSV cancellation was ignored for {count} channels.");
        }
        catch (OperationCanceledException)
        {
            if (await File.ReadAllTextAsync(exportPath) != "original-file-must-survive")
                report.Errors.Add("CSV cancellation damaged the original file.");
        }
        finally
        {
            File.Delete(exportPath);
        }
        report.Results.Add(new
        {
            Kind = "snapshot-export", Channels = count, SnapshotMs = snapshotWatch.Elapsed.TotalMilliseconds,
            MemoryBefore = memory, MemoryWithSnapshot = snapshotMemory, CancelMs = cancelWatch.Elapsed.TotalMilliseconds
        });
        Console.WriteLine($"{count} ch snapshot: {snapshotWatch.Elapsed.TotalMilliseconds:F1} ms; heap={snapshotMemory.ManagedMiB:F1} MiB; cancellation={cancelWatch.Elapsed.TotalMilliseconds:F1} ms");
    }

    private async Task RunLiveCaseAsync(int count)
    {
        Console.WriteLine($"Live UI soak: {count} channels, 2000 Hz, {options.DurationSeconds} seconds");
        var catalog = new SymbolCatalog
        {
            Elf = "stress-synthetic.elf", Format = "newscope-symbols-v1", RamStart = 0x20000000,
            RamSize = (uint)(count * 4), VariableCount = count,
            Variables = Enumerable.Range(0, count).Select(index => Symbol(index)).ToList()
        };
        var settings = new AppSettings
        {
            SampleRateHz = 2000, WindowSeconds = 60, PaneCount = 4,
            Theme = AppSettingsStore.Current.Theme, Language = Localization.Language,
            Channels = catalog.Variables.Select((symbol, index) => new ChannelWorkspaceEntry
                { Name = symbol.Name, Address = symbol.Address, PaneIndex = index % 4 }).ToList()
        };
        var source = new MeasuringSource();
        var model = new MainViewModel(false, settings, () => catalog, (_, _) => source);
        var window = new MainWindow { DataContext = model };
        var plots = new[] { "Plot", "PlotB", "PlotC", "PlotD" }.Select(name => window.FindControl<WaveformPlot>(name)!).ToArray();
        var heartbeat = new List<double>();
        var inputTimes = new List<double>();
        var memory = new List<object>();
        var beatClock = Stopwatch.StartNew();
        var lastBeat = beatClock.Elapsed.TotalMilliseconds;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        var progressResult = new { Kind = "live-in-progress", Channels = count, Memory = memory };
        timer.Tick += (_, _) =>
        {
            var now = beatClock.Elapsed.TotalMilliseconds;
            heartbeat.Add(now - lastBeat);
            lastBeat = now;
        };
        try
        {
            await model.ConnectCommand.ExecuteAsync(null);
            await model.PauseCommand.ExecuteAsync(null);
            var capacity = SampleMemoryBudget.GetCapacity(count, 2000);
            await Task.Run(() => Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, channel =>
            {
                for (var i = 0; i < capacity; i++) model.Channels[channel].Buffer.Add(i / 2000.0, Value(i, channel));
            }));
            // Preload a full history without waiting a minute. Only the test
            // fixture adjusts the monotonic clock before acquisition resumes.
            var acquisition = (AcquisitionController)Field(model, "_acquisition")!;
            var clock = (Stopwatch)Field(acquisition, "_clock")!;
            clock.Stop();
            typeof(Stopwatch).GetField("_elapsed", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(clock, (long)(capacity / 2000.0 * Stopwatch.Frequency));
            Collect();
            window.Show();
            window.Width = 1480;
            window.Height = 900;
            await Task.Delay(500);
            source.ResetMetrics();
            var initialFrames = acquisition.GetMetrics().FrameCount;
            var initialAllocated = GC.GetTotalAllocatedBytes();
            var initialCollections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
            using var process = Process.GetCurrentProcess();
            var initialCpu = process.TotalProcessorTime;
            await model.ToggleRunCommand.ExecuteAsync(null);
            lastBeat = beatClock.Elapsed.TotalMilliseconds;
            timer.Start();
            var duration = Stopwatch.StartNew();
            var iterations = 0;
            var nextMemory = 0.0;
            report.Results.Add(progressResult);
            Task? export = null;
            var exportPath = Path.ChangeExtension(options.Output, ".live-cancel.csv");
            await File.WriteAllTextAsync(exportPath, "preserve-original");
            while (duration.Elapsed.TotalSeconds < options.DurationSeconds)
            {
                var plot = plots[iterations % plots.Length];
                var input = Stopwatch.StartNew();
                if (iterations % 80 == 0) plot.FitAll();
                Wheel(plot, iterations % 64 < 32 ? 1 : -1);
                Move(plot, Center(plot));
                if (iterations % 8 == 0) Drag(plot, new Vector(iterations % 16 == 0 ? -25 : 25, 0));
                if (iterations % 13 == 0) Wheel(plot, iterations % 26 == 0 ? 1 : -1, yAxis: true);
                inputTimes.Add(input.Elapsed.TotalMilliseconds);
                AssertViewport(plot);
                if (duration.Elapsed.TotalSeconds >= nextMemory)
                {
                    memory.Add(new { Seconds = duration.Elapsed.TotalSeconds, Metrics = Memory(), Frames = acquisition.GetMetrics().FrameCount });
                    nextMemory += 5;
                    File.WriteAllText(options.Output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                }
                if (export is null && duration.Elapsed.TotalSeconds >= Math.Min(20, options.DurationSeconds / 3.0))
                {
                    export = model.ExportCsvAsync(exportPath, "stress.csv");
                    _ = CancelExportLaterAsync(model);
                }
                iterations++;
                await Task.Delay(50);
            }
            duration.Stop();
            timer.Stop();
            await model.PauseCommand.ExecuteAsync(null);
            if (export is not null) await export;
            if (await File.ReadAllTextAsync(exportPath) != "preserve-original")
                report.Errors.Add("Live export cancellation overwrote the original file.");
            File.Delete(exportPath);
            var metrics = acquisition.GetMetrics();
            var memoryBeforeCollection = Memory();
            Collect();
            var memoryAfterCollection = Memory();
            process.Refresh();
            var result = new
            {
                Kind = "live-ui", Channels = count, RequestedHz = 2000, DurationSeconds = duration.Elapsed.TotalSeconds,
                Frames = metrics.FrameCount - initialFrames, ActualHz = (metrics.FrameCount - initialFrames) / duration.Elapsed.TotalSeconds,
                UiHeartbeat = Timings.From(heartbeat), Inputs = Timings.From(inputTimes), Iterations = iterations,
                MaxAcquisitionGapMs = source.MaxGapMs,
                CpuCoreEquivalents = (process.TotalProcessorTime - initialCpu).TotalSeconds / duration.Elapsed.TotalSeconds,
                AllocatedMiBPerSecond = (GC.GetTotalAllocatedBytes() - initialAllocated) / (1024.0 * 1024) / duration.Elapsed.TotalSeconds,
                GcCollections = new[] { GC.CollectionCount(0) - initialCollections[0], GC.CollectionCount(1) - initialCollections[1], GC.CollectionCount(2) - initialCollections[2] },
                Memory = memory, MemoryBeforeCollection = memoryBeforeCollection, MemoryAfterCollection = memoryAfterCollection,
                Error = model.ErrorMessage
            };
            report.Results.Remove(progressResult);
            report.Results.Add(result);
            if (model.HasError) report.Errors.Add("Live acquisition: " + model.ErrorMessage);
            Console.WriteLine($"Live: {result.ActualHz:F1} Hz, UI heartbeat {result.UiHeartbeat}, input {result.Inputs}, iterations={iterations}, max read gap={source.MaxGapMs:F1} ms");
        }
        finally
        {
            timer.Stop();
            await model.ShutdownAsync();
            window.DataContext = null;
            window.Close();
        }
    }

    private static SymbolVariable Symbol(int index) => new()
    {
        Name = $"stress.group{index / 16:000}.signal{index:0000}", Address = 0x20000000 + (uint)index * 4,
        Size = 4, Type = "float", Kind = "float", Signed = true
    };

    private static ChannelViewModel Channel(int index, int capacity) => new(Symbol(index), index, capacity: capacity);
    private static double Value(int index, int channel) => index % 9973 == 100 ? 10000 :
        index % 9973 == 101 ? -10000 : Math.Sin(index * 0.007 + channel * 0.1) * (100 + channel);

    private static object? Field(object instance, string name) => instance.GetType()
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);

    private static void Arrange(WaveformPlot plot, int width, int height)
    {
        if (TopLevel.GetTopLevel(plot) is null)
        {
            var host = new Window { Content = plot, Width = width, Height = height, ShowInTaskbar = false };
            host.ApplyTemplate();
            host.Measure(new Size(width, height));
            host.Arrange(new Rect(0, 0, width, height));
            host.UpdateLayout();
            TestHosts.Add(plot, host);
        }
        plot.Measure(new Size(width, height));
        plot.Arrange(new Rect(0, 0, width, height));
    }

    private static Point Center(WaveformPlot plot)
    {
        var rect = (Rect)Field(plot, "_plotRect")!;
        return rect.Center;
    }

    private static void Wheel(WaveformPlot plot, double delta, bool yAxis = false)
    {
        var center = Center(plot);
        var position = yAxis ? new Point(50, center.Y) : center;
        var input = InputPosition(plot, position);
        InputMethods["OnPointerWheelChanged"].Invoke(plot,
            [new PointerWheelEventArgs(plot, TestPointer, input.Root, input.Position, 0, NoButtons, KeyModifiers.None, new Vector(0, delta))]);
    }

    private static void Move(WaveformPlot plot, Point position)
    {
        var input = InputPosition(plot, position);
        InputMethods["OnPointerMoved"].Invoke(plot,
            [new PointerEventArgs(InputElement.PointerMovedEvent, plot, TestPointer, input.Root, input.Position, 0, NoButtons, KeyModifiers.None)]);
    }

    private static void Exit(WaveformPlot plot) => InputMethods["OnPointerExited"].Invoke(plot,
        [new PointerEventArgs(InputElement.PointerExitedEvent, plot, TestPointer, plot, default, 0, NoButtons, KeyModifiers.None)]);

    private static void Drag(WaveformPlot plot, Vector delta)
    {
        var start = Center(plot);
        var first = InputPosition(plot, start);
        var last = InputPosition(plot, start + delta);
        InputMethods["OnPointerPressed"].Invoke(plot,
            [new PointerPressedEventArgs(plot, TestPointer, first.Root, first.Position, 0, LeftDown, KeyModifiers.None, 1)]);
        InputMethods["OnPointerMoved"].Invoke(plot,
            [new PointerEventArgs(InputElement.PointerMovedEvent, plot, TestPointer, last.Root, last.Position, 0, LeftDown, KeyModifiers.None)]);
        InputMethods["OnPointerReleased"].Invoke(plot,
            [new PointerReleasedEventArgs(plot, TestPointer, last.Root, last.Position, 0, NoButtons, KeyModifiers.None, MouseButton.Left)]);
    }

    private static (Visual Root, Point Position) InputPosition(WaveformPlot plot, Point position)
    {
        var root = TopLevel.GetTopLevel(plot) as Visual ?? plot;
        return (root, plot.TranslatePoint(position, root) ?? position);
    }

    private static void CloseHost(WaveformPlot plot)
    {
        if (TestHosts.Remove(plot, out var host))
        {
            host.Content = null;
            host.Close();
        }
    }

    private static void AssertViewport(WaveformPlot plot)
    {
        var start = (double)Field(plot, "_viewStart")!;
        var end = (double)Field(plot, "_viewEnd")!;
        if (!double.IsFinite(start) || !double.IsFinite(end) || end <= start || start < 0)
            throw new InvalidOperationException($"Invalid viewport: {start}..{end}");
        if ((bool)Field(plot, "_manualY")! &&
            (!double.IsFinite((double)Field(plot, "_manualYMin")!) || !double.IsFinite((double)Field(plot, "_manualYMax")!)))
            throw new InvalidOperationException("Manual Y range is non-finite.");
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static MemorySample Memory()
    {
        using var process = Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo();
        const double mib = 1024.0 * 1024;
        return new MemorySample(GC.GetTotalMemory(false) / mib, process.PrivateMemorySize64 / mib,
            process.WorkingSet64 / mib, info.TotalCommittedBytes / mib, info.FragmentedBytes / mib);
    }

    private sealed record MemorySample(double ManagedMiB, double PrivateMiB, double WorkingSetMiB, double GcCommittedMiB, double FragmentedMiB);
    private static async Task CancelExportLaterAsync(MainViewModel model)
    {
        await Task.Delay(100);
        model.CancelExportCommand.Execute(null);
    }

    private sealed class MeasuringSource : IValueSource
    {
        private readonly DemoValueSource _inner = new();
        private long _lastRead;
        private long _maxGap;
        public string Description => _inner.Description;
        public double MaxGapMs => Volatile.Read(ref _maxGap) * 1000.0 / Stopwatch.Frequency;
        public void Open() => _inner.Open();
        public void Close() => _inner.Close();
        public void Dispose() => _inner.Dispose();
        public void ResetMetrics() { _lastRead = 0; _maxGap = 0; }
        public double[] Read(IReadOnlyList<ISampleChannel> channels)
        {
            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Exchange(ref _lastRead, now);
            if (previous != 0) _maxGap = Math.Max(_maxGap, now - previous);
            return _inner.Read(channels);
        }
    }
    private sealed class InlineProgress(Action<double> action) : IProgress<double>
    {
        public void Report(double value) => action(value);
    }
}

internal sealed record Timings(int Count, double P50Ms, double P95Ms, double MaxMs)
{
    public static Timings From(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? new Timings(0, 0, 0, 0) :
            new Timings(sorted.Length, sorted[(sorted.Length - 1) / 2], sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1], sorted[^1]);
    }

    public static Timings Measure(int count, Action action)
    {
        action();
        return From(Enumerable.Range(0, count).Select(_ =>
        {
            var watch = Stopwatch.StartNew();
            action();
            return watch.Elapsed.TotalMilliseconds;
        }));
    }

    public override string ToString() => $"p50={P50Ms:F2} ms p95={P95Ms:F2} ms max={MaxMs:F2} ms";
}
