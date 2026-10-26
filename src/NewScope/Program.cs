using Avalonia;
using Avalonia.Logging;
using Avalonia.Threading;
using NewScope.Services;
using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace NewScope;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        if (JLinkWorker.IsWorker(args))
        {
            return JLinkWorker.Run(args);
        }

        if (!SingleInstanceGuard.TryAcquire(out var singleInstance))
        {
            if (!SingleInstanceGuard.TryActivateExistingInstance())
            {
                SingleInstanceGuard.ShowAlreadyRunningMessage();
            }

            return 0;
        }

        using var singleInstanceGuard = singleInstance;
        AppLog.Initialize();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            DiagnosticsService.HandleUnhandledException(e.ExceptionObject as Exception, "AppDomain");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            DiagnosticsService.HandleUnhandledException(e.Exception, "TaskScheduler");
        Dispatcher.UIThread.UnhandledException += (_, e) =>
            DiagnosticsService.HandleUnhandledException(e.Exception, "Dispatcher");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            AppLog.Info("Process exit");

        DiagnosticsService.InstallNativeCrashHandler();
        AppLog.Info($"Starting NewScope {AppVersion()} on {RuntimeInformation.OSDescription} " +
                    $"({RuntimeInformation.ProcessArchitecture}), J-Link DLL: {JLinkNative.DllPath}");
        AppLog.Info(
            $"Process: pid={Environment.ProcessId}, session={AppLog.SessionId}, " +
            $"framework={RuntimeInformation.FrameworkDescription}, cwd={Environment.CurrentDirectory}");
        AppLog.Info($"Command line: {Environment.CommandLine}");
        if (AppLog.PreviousSessionAbnormal)
        {
            AppLog.Warn(
                "Previous session did not exit cleanly. " +
                $"Previous log is under {AppLog.LogsDirectory}; check crash/hang files in {DiagnosticsService.DumpsDirectory}.");
        }

        var normalExit = false;
        var exitCode = 0;
        try
        {
            var builder = BuildAvaloniaApp();
            Logger.Sink = new AvaloniaLogSink();
            exitCode = builder.StartWithClassicDesktopLifetime(args);
            normalExit = true;
        }
        catch (Exception ex)
        {
            DiagnosticsService.HandleUnhandledException(ex, "Startup");
            throw;
        }
        finally
        {
            if (normalExit)
            {
                AppLog.MarkCleanExit();
            }

            AppLog.Info($"Application lifetime ended (normalExit={normalExit})");
        }

        return exitCode;
    }

    private static string AppVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();
#if DEBUG
        builder.WithDeveloperTools();
#endif
        return builder.WithInterFont().LogToTrace();
    }
}
