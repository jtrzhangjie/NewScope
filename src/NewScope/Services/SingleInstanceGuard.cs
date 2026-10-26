using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NewScope.Services;

public sealed class SingleInstanceGuard : IDisposable
{
    private const string DefaultMutexName = @"Local\NewScope.App.SingleInstance";
    private Mutex? _mutex;

    private SingleInstanceGuard(Mutex mutex) => _mutex = mutex;

    public static bool TryAcquire(out SingleInstanceGuard? guard) =>
        TryAcquire(DefaultMutexName, out guard);

    public static bool TryAcquire(string name, out SingleInstanceGuard? guard)
    {
        guard = null;
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
            if (createdNew)
            {
                guard = new SingleInstanceGuard(mutex);
                return true;
            }

            try
            {
                if (mutex.WaitOne(0))
                {
                    guard = new SingleInstanceGuard(mutex);
                    return true;
                }
            }
            catch (AbandonedMutexException)
            {
                guard = new SingleInstanceGuard(mutex);
                return true;
            }

            return false;
        }
        catch
        {
            mutex?.Dispose();
            return true;
        }
        finally
        {
            if (guard is null)
            {
                mutex?.Dispose();
            }
        }
    }

    public static bool TryActivateExistingInstance()
    {
        using var current = Process.GetCurrentProcess();
        var deadline = Environment.TickCount64 + 2000;
        do
        {
            foreach (var process in Process.GetProcessesByName(current.ProcessName))
            {
                try
                {
                    if (process.Id == current.Id)
                    {
                        continue;
                    }

                    process.Refresh();
                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero ||
                        !process.MainWindowTitle.Contains("NewScope", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    ShowWindowAsync(handle, 9);
                    SetForegroundWindow(handle);
                    return true;
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            Thread.Sleep(100);
        }
        while (Environment.TickCount64 < deadline);

        return false;
    }

    public static void ShowAlreadyRunningMessage()
    {
        MessageBox(
            IntPtr.Zero,
            "NewScope is already running.\n\nNewScope 已在运行。请切换到已打开的窗口。",
            "NewScope",
            0x00000040);
    }

    public void Dispose()
    {
        var mutex = _mutex;
        _mutex = null;
        if (mutex is null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        catch
        {
        }

        mutex.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
