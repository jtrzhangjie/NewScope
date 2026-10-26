using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace NewScope.Services;

internal sealed class JLinkWorkerSession : IJLinkSession
{
    private const int OpenTimeoutMilliseconds = 20_000;
    private const int ReadTimeoutMilliseconds = 4_000;
    private const int CloseTimeoutMilliseconds = 2_000;
    private const int MaxPayloadBytes = 4 * 1024 * 1024;

    private readonly object _gate = new();
    private Process? _process;
    private BinaryReader? _reader;
    private BinaryWriter? _writer;
    private PendingRequest? _pending;
    private int _workerGeneration;
    private bool _open;
    private bool _disposed;

    public bool IsOpen => _open;
    public string Description { get; private set; } = "J-Link not connected";
    public int ActualSpeedKHz { get; private set; }
    public int MaxSpeedKHz { get; private set; }
    public int? WorkerProcessId => _process?.Id;

    public void Open(string device, int speedKHz, bool resumeAfterAttach = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Close();
        StartWorker();
        var response = Send(
            JLinkWorkerProtocol.OpenCommand,
            writer =>
            {
                writer.Write(device);
                writer.Write(speedKHz);
                writer.Write(resumeAfterAttach);
            },
            OpenTimeoutMilliseconds);

        if (response.Status != 0)
        {
            Close();
            throw new JLinkException(response.Message.Length == 0
                ? "J-Link worker could not open the probe."
                : response.Message);
        }

        _open = true;
        Description = response.Message.Length == 0 ? "J-Link connected" : response.Message;
        if (response.Payload.Length >= 8)
        {
            ActualSpeedKHz = BinaryPrimitives.ReadInt32LittleEndian(response.Payload);
            MaxSpeedKHz = BinaryPrimitives.ReadInt32LittleEndian(response.Payload.AsSpan(4));
        }
    }

    public void ReadMemory(uint address, byte[] buffer, int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_open)
        {
            throw new JLinkException("J-Link is not open.");
        }

        var response = Send(
            JLinkWorkerProtocol.ReadCommand,
            writer =>
            {
                writer.Write(address);
                writer.Write(length);
            },
            ReadTimeoutMilliseconds);

        if (response.Status != 0)
        {
            throw new JLinkException(response.Message.Length == 0
                ? $"J-Link worker read failed at 0x{address:X8}."
                : response.Message);
        }

        if (response.Payload.Length != length)
        {
            throw new JLinkException(
                $"J-Link worker returned {response.Payload.Length} bytes for 0x{address:X8}; expected {length}.");
        }

        Buffer.BlockCopy(response.Payload, 0, buffer, 0, length);
    }

    public void Close()
    {
        lock (_gate)
        {
            if (_process is null)
            {
                _open = false;
                return;
            }

            if (_open)
            {
                try
                {
                    _ = SendWithoutLock(JLinkWorkerProtocol.CloseCommand, null, CloseTimeoutMilliseconds);
                }
                catch
                {
                }
            }

            _open = false;
            StopWorker();
            ActualSpeedKHz = 0;
            MaxSpeedKHz = 0;
            Description = "J-Link released";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Close();
        _disposed = true;
    }

    private void StartWorker()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new JLinkException("Could not determine the NewScope executable for the probe worker.");
        }

        var generation = Interlocked.Increment(ref _workerGeneration);
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };

        if (IsDotnetHost(processPath))
        {
#pragma warning disable IL3000
            var assemblyPath = Assembly.GetEntryAssembly()?.Location;
#pragma warning restore IL3000
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                throw new JLinkException("Could not determine the NewScope assembly for the probe worker.");
            }

            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.ArgumentList.Add(JLinkWorker.Switch);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

        var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new JLinkException("Could not start the J-Link probe worker.");
        }

        _process = process;
        _writer = new BinaryWriter(process.StandardInput.BaseStream, Encoding.UTF8, leaveOpen: true);
        _reader = new BinaryReader(process.StandardOutput.BaseStream, Encoding.UTF8, leaveOpen: true);
        _ = Task.Run(() => ReadLoop(process, generation));
        _ = Task.Run(() => DrainStandardError(process));
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            if (_disposed)
            {
                return;
            }

            if (generation == Volatile.Read(ref _workerGeneration))
            {
                AppLog.Warn($"J-Link worker exited unexpectedly (pid={process.Id}, code={SafeExitCode(process)}).");
            }
            else
            {
                AppLog.Info($"J-Link worker stopped (pid={process.Id}).");
            }
        };
        AppLog.Info($"J-Link worker started (pid={process.Id})");
    }

    private WorkerResponse Send(byte command, Action<BinaryWriter>? write, int timeoutMilliseconds)
    {
        lock (_gate)
        {
            return SendWithoutLock(command, write, timeoutMilliseconds);
        }
    }

    private WorkerResponse SendWithoutLock(byte command, Action<BinaryWriter>? write, int timeoutMilliseconds)
    {
        var process = _process;
        var writer = _writer;
        if (process is null || writer is null || process.HasExited)
        {
            throw new JLinkException("J-Link worker is not running.");
        }

        var request = new PendingRequest(
            Volatile.Read(ref _workerGeneration),
            new TaskCompletionSource<WorkerResponse>(TaskCreationOptions.RunContinuationsAsynchronously));
        _pending = request;
        try
        {
            writer.Write(command);
            write?.Invoke(writer);
            writer.Flush();
            return request.Completion.Task
                .WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds))
                .GetAwaiter()
                .GetResult();
        }
        catch (TimeoutException)
        {
            StopWorker();
            throw new JLinkException($"J-Link worker did not respond within {timeoutMilliseconds} ms.");
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            StopWorker();
            throw new JLinkException($"J-Link worker communication failed: {exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(_pending, request))
            {
                _pending = null;
            }
        }
    }

    private void ReadLoop(Process process, int generation)
    {
        var reader = _reader;
        if (reader is null)
        {
            return;
        }

        try
        {
            while (true)
            {
                var status = reader.ReadInt32();
                var message = reader.ReadString();
                var length = reader.ReadInt32();
                if (length < 0 || length > MaxPayloadBytes)
                {
                    throw new InvalidDataException($"Invalid J-Link worker payload length {length}.");
                }

                var payload = reader.ReadBytes(length);
                if (payload.Length != length)
                {
                    throw new EndOfStreamException("J-Link worker closed the response stream.");
                }

                var request = _pending;
                if (request is not null && request.Generation == generation)
                {
                    request.Completion.TrySetResult(new WorkerResponse(status, message, payload));
                }
            }
        }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _workerGeneration) && !process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            }

            var request = _pending;
            if (request is not null && request.Generation == generation)
            {
                request.Completion.TrySetException(new JLinkException(
                    $"J-Link worker stopped responding: {exception.Message}"));
            }
        }
    }

    private void DrainStandardError(Process process)
    {
        try
        {
            _ = process.StandardError.ReadToEnd();
        }
        catch
        {
        }
    }

    private void StopWorker()
    {
        Interlocked.Increment(ref _workerGeneration);
        var process = _process;
        _process = null;
        var reader = _reader;
        var writer = _writer;
        _reader = null;
        _writer = null;
        _pending = null;

        try
        {
            reader?.Dispose();
        }
        catch
        {
        }

        try
        {
            writer?.Dispose();
        }
        catch
        {
        }

        if (process is not null)
        {
            try
            {
                if (!process.HasExited && !process.WaitForExit(1_500))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1_500);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static bool IsDotnetHost(string processPath) =>
        string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private sealed record PendingRequest(int Generation, TaskCompletionSource<WorkerResponse> Completion);

    private sealed record WorkerResponse(int Status, string Message, byte[] Payload);
}

internal static class JLinkWorkerProtocol
{
    public const byte OpenCommand = 1;
    public const byte ReadCommand = 2;
    public const byte CloseCommand = 3;
    public const byte PingCommand = 4;
}
