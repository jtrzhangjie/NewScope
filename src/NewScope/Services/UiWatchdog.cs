namespace NewScope.Services;

public sealed class UiWatchdog : IDisposable
{
    private static readonly TimeSpan HangThreshold = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RecoveryThreshold = TimeSpan.FromSeconds(2);

    private readonly Func<string> _contextProvider;
    private readonly Thread _thread;
    private long _lastBeat = Environment.TickCount64;
    private int _hangReported;
    private volatile bool _stopped;
    private bool _disposed;

    public UiWatchdog(Func<string> contextProvider)
    {
        _contextProvider = contextProvider;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "NewScope.UIWatchdog",
            Priority = ThreadPriority.BelowNormal
        };
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Beat();
        if (!_thread.IsAlive)
        {
            _thread.Start();
            AppLog.Info($"UI watchdog started (hang threshold {HangThreshold.TotalSeconds:0}s)");
        }
    }

    public void Beat()
    {
        Interlocked.Exchange(ref _lastBeat, Environment.TickCount64);
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopped = true;
        Beat();
        if (_thread.IsAlive && Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        AppLog.Info("UI watchdog stopped");
    }

    public void Dispose() => Stop();

    private void Loop()
    {
        while (!_stopped)
        {
            Thread.Sleep(1000);
            if (_stopped)
            {
                break;
            }

            var silence = TimeSpan.FromMilliseconds(
                Environment.TickCount64 - Interlocked.Read(ref _lastBeat));
            if (silence >= HangThreshold)
            {
                if (Interlocked.Exchange(ref _hangReported, 1) == 0)
                {
                    var context = ReadContext();
                    AppLog.Warn(
                        $"UI watchdog: UI unresponsive for {silence.TotalSeconds:0.0}s " +
                        $"(pid={Environment.ProcessId}, session={AppLog.SessionId})");
                    AppLog.Warn($"UI watchdog state before dump:{Environment.NewLine}{context}");
                    DiagnosticsService.CaptureHangDump(context);
                }
            }
            else if (silence < RecoveryThreshold &&
                     Interlocked.Exchange(ref _hangReported, 0) == 1)
            {
                AppLog.Info("UI watchdog: UI responsive again");
            }
        }
    }

    private string ReadContext()
    {
        try
        {
            return _contextProvider();
        }
        catch (Exception ex)
        {
            return $"Failed to collect UI context: {ex}";
        }
    }
}
