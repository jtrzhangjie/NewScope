namespace NewScope.Services;

public sealed class JLinkException(string message) : Exception(message);

public interface IJLinkSession : IDisposable
{
    string Description { get; }
    int ActualSpeedKHz { get; }
    int MaxSpeedKHz { get; }
    void Open(string device, int speedKHz, bool resumeAfterAttach = true);
    void Close();
    void ReadMemory(uint address, byte[] buffer, int length);
}

public sealed class JLinkSession : IJLinkSession
{
    private bool _open;
    private bool _disposed;

    public bool IsOpen => _open;
    public string Description { get; private set; } = "J-Link not connected";
    public int ActualSpeedKHz { get; private set; }
    public int MaxSpeedKHz { get; private set; }
    public int DllVersion { get; private set; }
    public int SerialNumber { get; private set; }
    public string Device { get; private set; } = "";

    public void Open(string device, int speedKHz, bool resumeAfterAttach = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_open)
        {
            return;
        }

        if (!JLinkNative.TryOpen(out var openError))
        {
            throw new JLinkException(openError ?? "Could not open the J-Link DLL.");
        }

        _open = true;
        try
        {
            DllVersion = JLinkNative.GetDllVersion();
            SerialNumber = JLinkNative.GetSerialNumber();
            Device = device;

            JLinkNative.ExecCommand("SetBatchMode = 1", out _);

            var interfaceResult = JLinkNative.SelectSwd();
            if (interfaceResult < 0)
            {
                throw new JLinkException($"Could not select SWD (J-Link error {interfaceResult}).");
            }

            JLinkNative.SetSpeed(50_000);
            var maxSpeedKHz = JLinkNative.GetSpeed();
            if (maxSpeedKHz is <= 0 or > 50_000)
            {
                maxSpeedKHz = 50_000;
            }

            var requestedSpeedKHz = Math.Clamp(speedKHz, 1, 50_000);
            JLinkNative.SetSpeed(requestedSpeedKHz);

            JLinkNative.ClearError();
            JLinkNative.ExecCommand($"Device = {device}", out var deviceError);

            if (!JLinkNative.IsConnected())
            {
                var connectResult = JLinkNative.Connect();
                if (connectResult < 0)
                {
                    throw new JLinkException(
                        $"Could not connect to {device} (J-Link error {connectResult}).{FormatDeviceError(deviceError)}");
                }
            }

            var actualSpeedKHz = JLinkNative.GetSpeed();
            if (actualSpeedKHz is <= 0 or > 50_000)
            {
                actualSpeedKHz = Math.Min(requestedSpeedKHz, maxSpeedKHz);
            }

            ActualSpeedKHz = Math.Min(actualSpeedKHz, maxSpeedKHz);
            MaxSpeedKHz = maxSpeedKHz;

            // A debugger attach can leave Cortex-M halted. The scope is a
            // passive observer, so let the firmware run again before sampling.
            if (resumeAfterAttach)
            {
                JLinkNative.Go();
            }

            var dllVersion = JLinkNative.FormatVersion(DllVersion);
            var speedNote = ActualSpeedKHz < requestedSpeedKHz
                ? $" (requested {requestedSpeedKHz / 1000.0:0.#} MHz; probe max {MaxSpeedKHz / 1000.0:0.#} MHz)"
                : "";
            Description =
                $"J-Link {dllVersion} · SN {SerialNumber} · {device} · " +
                $"SWD {ActualSpeedKHz / 1000.0:0.#} MHz{speedNote}";
        }
        catch
        {
            Close();
            throw;
        }
    }

    public void ReadMemory(uint address, byte[] buffer, int length)
    {
        if (!_open)
        {
            throw new JLinkException("J-Link is not open.");
        }

        var read = JLinkNative.ReadMemory(address, buffer, length);
        if (read < length)
        {
            throw new JLinkException(
                $"J-Link read failed at 0x{address:X8}: requested {length} bytes, got {read}.");
        }
    }

    public void Close()
    {
        if (!_open)
        {
            return;
        }

        _open = false;
        JLinkNative.Close();
        ActualSpeedKHz = 0;
        MaxSpeedKHz = 0;
        Description = "J-Link released";
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

    private static string FormatDeviceError(string error) =>
        string.IsNullOrWhiteSpace(error) ? "" : $" {error}";
}
