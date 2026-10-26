using System.Diagnostics;
using NewScope.Models;

namespace NewScope.Services;

public readonly record struct AcquisitionMetrics(long FrameCount, TimeSpan Elapsed, double ActualRate);

/// <summary>
/// Owns one acquisition task and serializes frame commits, resets and export
/// snapshots. Probe reads happen outside the gate so a blocked probe cannot
/// prevent clearing data or taking a snapshot.
/// </summary>
public sealed class AcquisitionController : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = new();
    private readonly Stopwatch _rateClock = new();
    private ISampleChannel[] _channels = [];
    private CancellationTokenSource? _cts;
    private Task _completion = Task.CompletedTask;
    private long _runId;
    private long _dataVersion;
    private long _frameCount;
    private long _rateFrames;
    private double _actualRate;
    private Exception? _lastError;
    private volatile int _sampleRateHz = 100;
    private bool _disposed;

    public int SampleRateHz
    {
        get => _sampleRateHz;
        set => _sampleRateHz = Math.Max(1, value);
    }

    public Task Completion
    {
        get { lock (_gate) { return _completion; } }
    }

    public long RunId
    {
        get { lock (_gate) { return _runId; } }
    }

    public bool IsRunning
    {
        get { lock (_gate) { return _cts is not null; } }
    }

    public Exception? LastError
    {
        get { lock (_gate) { return _lastError; } }
    }

    public void SetChannels(IReadOnlyList<ISampleChannel> channels)
    {
        lock (_gate)
        {
            _channels = channels.ToArray();
            _dataVersion++;
        }
    }

    public void Reset(bool startClock = true)
    {
        lock (_gate)
        {
            _dataVersion++;
            foreach (var channel in _channels)
            {
                channel.Buffer.Clear();
            }

            _clock.Reset();
            if (startClock)
            {
                _clock.Start();
            }
            _rateClock.Restart();
            _frameCount = 0;
            _rateFrames = 0;
            _actualRate = 0;
            _lastError = null;
        }
    }

    public AcquisitionMetrics GetMetrics()
    {
        // Display-only telemetry must not block the UI behind a large export
        // snapshot. Frame commits and resets still share the data gate.
        return new AcquisitionMetrics(Interlocked.Read(ref _frameCount), _clock.Elapsed, Volatile.Read(ref _actualRate));
    }

    public ChannelSnapshot[] CaptureSnapshot(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return CsvExporter.Capture(_channels, cancellationToken);
        }
    }

    public bool Start(IValueSource source)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cts is not null)
            {
                return false;
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            _lastError = null;
            _runId++;
            _clock.Start();
            _rateClock.Restart();
            _rateFrames = 0;
            // Capture the CTS and source, never mutable fields that StopAsync
            // or a subsequent connection could replace before the task starts.
            _completion = Task.Run(() => RunAsync(source, cts));
            return true;
        }
    }

    public async Task StopAsync()
    {
        Task completion;
        lock (_gate)
        {
            _cts?.Cancel();
            completion = _completion;
        }

        await completion.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        await StopAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(IValueSource source, CancellationTokenSource cts)
    {
        var token = cts.Token;
        Exception? error = null;
        Win32Timer.Begin();
        try
        {
            var pace = Stopwatch.StartNew();
            var nextDeadline = 0.0;
            while (!token.IsCancellationRequested)
            {
                ISampleChannel[] channels;
                long dataVersion;
                double sampleTime;
                lock (_gate)
                {
                    channels = _channels;
                    dataVersion = _dataVersion;
                    sampleTime = _clock.Elapsed.TotalSeconds;
                }

                if (channels.Length == 0)
                {
                    await Task.Delay(40, token).ConfigureAwait(false);
                    nextDeadline = pace.Elapsed.TotalSeconds;
                    continue;
                }

                var values = source.Read(channels);
                if (values.Length != channels.Length)
                {
                    throw new InvalidOperationException("Value source returned an unexpected number of samples.");
                }

                lock (_gate)
                {
                    // Discard an in-flight read from before a clear/reset or a
                    // channel change. No old timestamp can enter a new session.
                    if (dataVersion == _dataVersion && !token.IsCancellationRequested)
                    {
                        for (var i = 0; i < channels.Length; i++)
                        {
                            channels[i].Buffer.Add(sampleTime, values[i]);
                        }

                        _frameCount++;
                        _rateFrames++;
                        var elapsed = _rateClock.Elapsed.TotalSeconds;
                        if (elapsed >= 1)
                        {
                            _actualRate = _rateFrames / elapsed;
                            _rateFrames = 0;
                            _rateClock.Restart();
                        }
                    }
                }

                nextDeadline += 1.0 / _sampleRateHz;
                var delay = nextDeadline - pace.Elapsed.TotalSeconds;
                if (delay > 0.0008)
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);
                }
                else if (delay < -0.25)
                {
                    nextDeadline = pace.Elapsed.TotalSeconds;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            error = exception;
        }
        finally
        {
            Win32Timer.End();
            lock (_gate)
            {
                _lastError = error;
                _cts = null;
                cts.Dispose();
            }
        }
    }
}
