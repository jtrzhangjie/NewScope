using System.Text;

namespace NewScope.Services;

public static class AppLog
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private const int MaxArchives = 10;
    private static readonly object Gate = new();
    private static bool _initialized;

    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NewScope");

    public static string LogsDirectory { get; } = Path.Combine(DirectoryPath, "logs");

    public static string FilePath { get; } = Path.Combine(DirectoryPath, "newscope.log");

    private static string CleanExitMarkerPath { get; } = Path.Combine(DirectoryPath, "clean-exit.flag");

    public static string SessionId { get; } = Guid.NewGuid().ToString("N")[..8];

    public static bool PreviousSessionAbnormal { get; private set; }

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(DirectoryPath);
                Directory.CreateDirectory(LogsDirectory);
                var hadPreviousLog = File.Exists(FilePath) && new FileInfo(FilePath).Length > 0;
                var previousExitedCleanly = File.Exists(CleanExitMarkerPath);
                PreviousSessionAbnormal = hadPreviousLog && !previousExitedCleanly;
                TryDelete(CleanExitMarkerPath);
                ArchiveCurrent("previous-run");
            }
            catch
            {
            }
            finally
            {
                _initialized = true;
            }
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", message, exception);

    public static void Event(string category, string message)
    {
        Write("INFO", $"[{category}] {message}", null);
    }

    public static void MarkCleanExit()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(CleanExitMarkerPath, DateTime.UtcNow.ToString("O"));
        }
        catch
        {
        }
    }

    private static void Write(string level, string message, Exception? exception)
    {
        if (!Monitor.TryEnter(Gate, TimeSpan.FromSeconds(1)))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(DirectoryPath);
            Rotate();
            var builder = new StringBuilder();
            builder.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ");
            builder.Append("[T").Append(Environment.CurrentManagedThreadId).Append("] ");
            builder.Append("[P").Append(Environment.ProcessId).Append("] ");
            builder.Append(level).Append(' ').Append(message);
            if (exception is not null)
            {
                builder.Append(Environment.NewLine).Append(exception);
            }

            builder.AppendLine();
            File.AppendAllText(FilePath, builder.ToString());
        }
        catch
        {
        }
        finally
        {
            Monitor.Exit(Gate);
        }
    }

    private static void Rotate()
    {
        if (!File.Exists(FilePath) || new FileInfo(FilePath).Length < MaxBytes)
        {
            return;
        }

        ArchiveCurrent("rotate");
    }

    private static void ArchiveCurrent(string reason)
    {
        if (!File.Exists(FilePath) || new FileInfo(FilePath).Length == 0)
        {
            return;
        }

        Directory.CreateDirectory(LogsDirectory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var archivePath = Path.Combine(
            LogsDirectory,
            $"newscope-{stamp}-{Environment.ProcessId}-{reason}.log");
        if (File.Exists(archivePath))
        {
            archivePath = Path.Combine(
                LogsDirectory,
                $"newscope-{stamp}-{Environment.ProcessId}-{reason}-{SessionId}.log");
        }

        File.Move(FilePath, archivePath);
        PruneArchives();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void PruneArchives()
    {
        try
        {
            var archives = new DirectoryInfo(LogsDirectory).GetFiles("newscope-*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(MaxArchives)
                .ToArray();
            foreach (var file in archives)
            {
                file.Delete();
            }
        }
        catch
        {
        }
    }
}
