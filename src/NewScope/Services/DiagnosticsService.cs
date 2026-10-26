using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NewScope.Services;

public static class DiagnosticsService
{
    private const uint MiniDumpWithDataSegs = 0x00000001;
    private const uint MiniDumpWithUnloadedModules = 0x00000020;
    private const uint MiniDumpWithThreadInfo = 0x00001000;
    private const uint MiniDumpFlags = MiniDumpWithDataSegs | MiniDumpWithUnloadedModules | MiniDumpWithThreadInfo;

    private static readonly object CaptureGate = new();
    private static readonly object DumpGate = new();
    private static readonly object NativeFilterGate = new();
    private static UnhandledExceptionFilter? _nativeFilter;
    private static int _crashCaptureStarted;

    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NewScope");

    public static string DumpsDirectory { get; } = Path.Combine(DirectoryPath, "dumps");

    public static string HangLogPath { get; } = Path.Combine(DirectoryPath, "hang.log");

    public static void InstallNativeCrashHandler()
    {
        lock (NativeFilterGate)
        {
            if (_nativeFilter is not null)
            {
                return;
            }

            _nativeFilter = OnNativeUnhandledException;
            var previous = SetUnhandledExceptionFilter(_nativeFilter);
            AppLog.Info($"Native crash filter installed (previous=0x{previous.ToInt64():X})");
        }
    }

    public static void HandleUnhandledException(Exception? exception, string source)
    {
        AppLog.Error($"Unhandled exception ({source})", exception);

        if (!TryBeginCrashCapture())
        {
            return;
        }

        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var dumpPath = Path.Combine(DumpsDirectory, $"crash-{stamp}.dmp");
            var dumpWritten = TryWriteDump(dumpPath);
            AppendCrashLog(
                source,
                dumpPath,
                dumpWritten,
                exception?.ToString() ?? "No managed exception object supplied.",
                dumpPath + ".txt");
            PruneDumps();
        }
        catch
        {
        }
    }

    public static string? CaptureHangDump(string context)
    {
        if (!Monitor.TryEnter(DumpGate, TimeSpan.FromSeconds(5)))
        {
            AppLog.Warn("UI watchdog could not acquire dump lock");
            return null;
        }

        try
        {
            Directory.CreateDirectory(DumpsDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var dumpPath = Path.Combine(DumpsDirectory, $"hang-{stamp}.dmp");
            var textPath = dumpPath + ".txt";
            var boundedContext = context.Length > 8000 ? context[..8000] : context;
            var header =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] UI unresponsive{Environment.NewLine}" +
                $"PID={Environment.ProcessId} Session={AppLog.SessionId}{Environment.NewLine}" +
                $"Dump=pending {dumpPath}{Environment.NewLine}";
            var pendingSummary = header + boundedContext + Environment.NewLine;
            File.WriteAllText(textPath, pendingSummary);
            File.AppendAllText(HangLogPath, pendingSummary + Environment.NewLine);

            var dumpWritten = TryWriteDump(dumpPath);
            var result =
                $"Dump={(dumpWritten ? dumpPath : "failed")}{Environment.NewLine}" +
                $"Completed: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}{Environment.NewLine}";
            File.AppendAllText(textPath, result);
            File.AppendAllText(HangLogPath, result + Environment.NewLine);
            AppLog.Warn(
                $"UI watchdog dump: {(dumpWritten ? dumpPath : "failed")}; " +
                $"context written to {textPath}");
            PruneDumps();
            return dumpWritten ? dumpPath : null;
        }
        catch (Exception ex)
        {
            AppLog.Error("UI watchdog dump failed", ex);
            return null;
        }
        finally
        {
            Monitor.Exit(DumpGate);
        }
    }

    public static string CreateBundle(string destinationPath, string? uiDetails = null)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        ExportDiagnostics(destinationPath, uiDetails, "exported");
        return destinationPath;
    }

    public static string ExportDiagnostics(string destinationPath, string? uiDetails, string reason)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        AddFile(archive, AppLog.FilePath, "logs/newscope.log");
        AddFiles(archive, AppLog.LogsDirectory, "newscope-*.log", "logs/archive", 10);
        AddFile(archive, AppLog.FilePath + ".1", "logs/newscope.log.1");
        AddFile(archive, Path.Combine(DirectoryPath, "crash.log"), "logs/crash.log");
        AddFile(archive, HangLogPath, "logs/hang.log");
        AddFile(archive, AppSettingsStore.FilePath, "settings.json");

        if (Directory.Exists(DumpsDirectory))
        {
            var dumps = new DirectoryInfo(DumpsDirectory).GetFiles("*")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(8);
            foreach (var dump in dumps)
            {
                AddFile(archive, dump.FullName, $"dumps/{dump.Name}");
            }
        }

        AddText(archive, "system.txt", BuildSystemInfo(uiDetails, reason));
        AppLog.Info($"Diagnostics bundle written: {destinationPath} ({reason})");
        return destinationPath;
    }

    private static string BuildSystemInfo(string? uiDetails, string reason)
    {
        var builder = new StringBuilder();
        using var process = Process.GetCurrentProcess();
        builder.AppendLine($"NewScope {Version}");
        builder.AppendLine($"Bundle reason: {reason}");
        builder.AppendLine($"Log session: {AppLog.SessionId}");
        builder.AppendLine($"UTC: {DateTime.UtcNow:O}");
        builder.AppendLine($"Process started: {process.StartTime:O}");
        builder.AppendLine($"Uptime: {DateTime.Now - process.StartTime}");
        builder.AppendLine($"Working set: {process.WorkingSet64 / (1024 * 1024):N0} MB");
        builder.AppendLine($"PID: {Environment.ProcessId}");
        builder.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        builder.AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}");
        builder.AppendLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine($"OS architecture: {RuntimeInformation.OSArchitecture}");
        builder.AppendLine($"Processors: {Environment.ProcessorCount}");
        builder.AppendLine($"Current directory: {Environment.CurrentDirectory}");
        builder.AppendLine($"Command line: {Environment.CommandLine}");
        builder.AppendLine($"Culture: {System.Globalization.CultureInfo.CurrentCulture.Name}");
        builder.AppendLine($"Log file: {AppLog.FilePath}");
        builder.AppendLine($"Log archive directory: {AppLog.LogsDirectory}");
        builder.AppendLine($"Dump directory: {DumpsDirectory}");
        builder.AppendLine($"J-Link DLL: {JLinkNative.DllPath}");
        builder.AppendLine($"User: {Environment.UserName}");
        builder.AppendLine();
        builder.AppendLine("UI state:");
        builder.AppendLine(string.IsNullOrWhiteSpace(uiDetails) ? "(not supplied)" : uiDetails);
        return builder.ToString();
    }

    private static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

    private static bool TryBeginCrashCapture()
    {
        lock (CaptureGate)
        {
            if (_crashCaptureStarted != 0)
            {
                return false;
            }

            _crashCaptureStarted = 1;
            return true;
        }
    }

    private static int OnNativeUnhandledException(IntPtr exceptionPointers)
    {
        try
        {
            if (TryBeginCrashCapture())
            {
                Directory.CreateDirectory(DirectoryPath);
                Directory.CreateDirectory(DumpsDirectory);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                var dumpPath = Path.Combine(DumpsDirectory, $"crash-native-{stamp}.dmp");
                var dumpWritten = TryWriteDump(dumpPath);
                var details = DescribeNativeException(exceptionPointers);
                AppendCrashLog(
                    "NativeUnhandledExceptionFilter",
                    dumpPath,
                    dumpWritten,
                    details,
                    dumpPath + ".txt");
                PruneDumps();
                AppLog.Error($"Native unhandled exception captured: {details}");
            }
        }
        catch
        {
        }

        return 0;
    }

    private static string DescribeNativeException(IntPtr exceptionPointers)
    {
        try
        {
            var record = Marshal.ReadIntPtr(exceptionPointers);
            if (record == IntPtr.Zero)
            {
                return "Unhandled native exception; exception record unavailable.";
            }

            var code = unchecked((uint)Marshal.ReadInt32(record));
            var address = Marshal.ReadIntPtr(record, 16);
            var parameterCount = Marshal.ReadInt32(record, 24);
            var name = code switch
            {
                0xC0000005 => "ACCESS_VIOLATION",
                0xC00000FD => "STACK_OVERFLOW",
                0xC0000409 => "STACK_BUFFER_OVERRUN / fail-fast",
                0x80000003 => "BREAKPOINT",
                0xE0434352 => "CLR exception",
                _ => "unknown"
            };
            var details = new StringBuilder();
            details.Append($"Unhandled native exception: code=0x{code:X8} ({name}), ");
            details.Append($"address=0x{address.ToInt64():X}");
            if (parameterCount > 0)
            {
                details.Append(", parameters=");
                var count = Math.Min(parameterCount, 4);
                for (var i = 0; i < count; i++)
                {
                    var value = Marshal.ReadIntPtr(record, 32 + (i * IntPtr.Size));
                    if (i > 0)
                    {
                        details.Append(',');
                    }

                    details.Append($"0x{value.ToInt64():X}");
                }
            }

            details.AppendLine();
            details.AppendLine("Inspect the minidump thread stacks; code/address are captured above.");
            return details.ToString();
        }
        catch (Exception ex)
        {
            return $"Unhandled native exception; failed to read exception record: {ex.Message}";
        }
    }

    private static void AppendCrashLog(
        string source,
        string dumpPath,
        bool dumpWritten,
        string details,
        string detailsPath)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var line =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] NewScope {Version} " +
                $"session={AppLog.SessionId} pid={Environment.ProcessId} " +
                $"{RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture} " +
                $"J-Link={JLinkNative.DllPath}{Environment.NewLine}" +
                $"Source={source}; Dump={(dumpWritten ? dumpPath : "failed")}{Environment.NewLine}" +
                $"{details}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(DirectoryPath, "crash.log"), line);
            File.WriteAllText(
                detailsPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] PID={Environment.ProcessId} " +
                $"Session={AppLog.SessionId}{Environment.NewLine}{line}");
        }
        catch
        {
        }
    }

    private static void AddFile(ZipArchive archive, string path, string entryName)
    {
        try
        {
            if (File.Exists(path))
            {
                archive.CreateEntryFromFile(path, entryName, CompressionLevel.Optimal);
            }
        }
        catch
        {
        }
    }

    private static void AddFiles(
        ZipArchive archive,
        string directory,
        string pattern,
        string entryDirectory,
        int limit)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var file in new DirectoryInfo(directory).GetFiles(pattern)
                         .OrderByDescending(candidate => candidate.LastWriteTimeUtc)
                         .Take(limit))
            {
                AddFile(archive, file.FullName, $"{entryDirectory}/{file.Name}");
            }
        }
        catch
        {
        }
    }

    private static void AddText(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, new UTF8Encoding(false));
        writer.Write(content);
    }

    internal static bool TryWriteDump(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? DirectoryPath);
            using var process = Process.GetCurrentProcess();
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            return MiniDumpWriteDump(
                process.Handle,
                (uint)process.Id,
                stream.SafeFileHandle,
                MiniDumpFlags,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }

    private static void PruneDumps()
    {
        try
        {
            var files = new DirectoryInfo(DumpsDirectory).GetFiles("*")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(10)
                .ToArray();
            foreach (var file in files)
            {
                file.Delete();
            }
        }
        catch
        {
        }
    }

    private delegate int UnhandledExceptionFilter(IntPtr exceptionPointers);

    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        SafeFileHandle hFile,
        uint dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr SetUnhandledExceptionFilter(UnhandledExceptionFilter filter);

}
