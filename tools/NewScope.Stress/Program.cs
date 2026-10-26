using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Styling;
using NewScope.Services;

namespace NewScope.Stress;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("NewScope.Stress --channels 32,128,256,512 --frames 120 --duration 300 --output <report.json> [--ui-only] [--root <project-root>]");
            Console.WriteLine("Use tools/stress.ps1 to run history/render and live UI cases in separate processes.");
            return 0;
        }

        var options = StressOptions.Parse(args);
        var report = new StressReport
        {
            StartedUtc = DateTimeOffset.UtcNow,
            Options = options,
            ProcessorCount = Environment.ProcessorCount,
            Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            UiHashes = UiHashes(options.Root)
        };
        Directory.CreateDirectory(Path.GetDirectoryName(options.Output)!);
        AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
        Localization.Initialize();
        Application.Current!.RequestedThemeVariant = string.Equals(AppSettingsStore.Current.Theme, "Light", StringComparison.OrdinalIgnoreCase)
            ? ThemeVariant.Light : ThemeVariant.Dark;
        AvaloniaSynchronizationContext.InstallIfNeeded();
        using var stop = new CancellationTokenSource();
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            report.Errors.Add("Dispatcher: " + e.Exception);
            e.Handled = true;
        };
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await new StressSuite(options, report).RunAsync();
            }
            catch (Exception exception)
            {
                report.Errors.Add(exception.ToString());
                Console.WriteLine(exception);
            }
            finally
            {
                report.FinishedUtc = DateTimeOffset.UtcNow;
                report.UiUnchanged = UiHashes(options.Root).OrderBy(pair => pair.Key)
                    .SequenceEqual(report.UiHashes.OrderBy(pair => pair.Key));
                if (!report.UiUnchanged) report.Errors.Add("Approved UI resources changed during the run.");
                File.WriteAllText(options.Output, JsonSerializer.Serialize(report,
                    new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Report: {options.Output}; errors={report.Errors.Count}");
                stop.Cancel();
            }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
        return report.Errors.Count == 0 ? 0 : 1;
    }

    private static Dictionary<string, string> UiHashes(string root) =>
        new[] { "src/NewScope/App.axaml", "src/NewScope/Views/MainWindow.axaml", "src/NewScope/Services/Localization.cs" }
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "src/NewScope/Assets"), "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')))
            .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, path)))));
}

internal sealed record StressOptions(string Root, string Output, int[] Channels, int DurationSeconds, int Frames, bool UiOnly)
{
    public static StressOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        var root = Path.GetFullPath(Value("--root") ?? Environment.CurrentDirectory);
        var output = Path.GetFullPath(Value("--output") ?? Path.Combine(root, "dist", "stress", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"));
        var channels = (Value("--channels") ?? "32,128,256,512").Split(',').Select(int.Parse).ToArray();
        var duration = int.Parse(Value("--duration") ?? "120");
        var frames = int.Parse(Value("--frames") ?? "60");
        if (channels.Length == 0 || channels.Any(count => count is < 1 or > 4096) || duration is < 0 or > 86400 || frames is < 0 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(args), "Invalid stress limits.");
        return new StressOptions(root, output, channels, duration, frames, args.Contains("--ui-only"));
    }
}

internal sealed class StressReport
{
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset FinishedUtc { get; set; }
    public required StressOptions Options { get; init; }
    public int ProcessorCount { get; init; }
    public string Framework { get; init; } = "";
    public string OS { get; init; } = "";
    public Dictionary<string, string> UiHashes { get; init; } = [];
    public bool UiUnchanged { get; set; }
    public List<object> Results { get; } = [];
    public List<string> Errors { get; } = [];
}

internal sealed partial class StressSuite(StressOptions options, StressReport report)
{
    public async Task RunAsync()
    {
        if (!options.UiOnly) RunEdgeCases();
        foreach (var count in options.UiOnly ? [] : options.Channels)
        {
            await RunHistoryCaseAsync(count);
        }
        if (options.DurationSeconds > 0)
        {
            await RunLiveCaseAsync(options.Channels.Contains(256) ? 256 : options.Channels.Max());
        }
    }
}
